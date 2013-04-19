﻿(*

Copyright 2010 Oliver Friedmann, Martin Lange
Copyright 2012-2013 Jack Pappas

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*)

namespace FSharpLex.SpecializedCollections

open System
open System.Collections.Generic
open System.Diagnostics
open OptimizedClosures
open ExtCore
open ExtCore.Collections
open ExtCore.Control


[<AutoOpen>]
module internal Constants =
    //
    let [<Literal>] defaultStackCapacity = 16


(*  NOTE :  The core functions implementing the AVL tree algorithm were extracted into OCaml
            from the "AVL Trees" theory in the Archive of Formal Proofs:
                http://afp.sourceforge.net/entries/AVL-Trees.shtml
            using Isabelle/HOL 2012. The extracted code was adapted to F# (e.g., by adjusting
            the formatting, fixing incomplete pattern-matches), then the supporting functions
            (e.g., 'fold', 'iter') were implemented.
            
            The DIET code was ported from Friedmann and Lange's 'camldiets' code, which is
            based on their paper "More on Balanced Diets". Their code was adapted to F# and
            the AVL tree extracted from Isabelle/HOL, then specialized for the 'char' type. *)

/// AVL Tree.
[<NoEquality; NoComparison>]
[<CompilationRepresentation(CompilationRepresentationFlags.UseNullAsTrueValue)>]
type internal AvlTree<'T when 'T : comparison> =
    /// Empty tree.
    | Empty
    /// Node.
    // Left-Child, Right-Child, Value, Height
    | Node of AvlTree<'T> * AvlTree<'T> * 'T * uint32

    /// Implementation. Returns the height of a AvlTree.
    static member private ComputeHeightRec (tree : AvlTree<'T>) cont =
        match tree with
        | Empty ->
            cont 0u
        | Node (l, r, _, _) ->
            AvlTree.ComputeHeightRec l <| fun height_l ->
            AvlTree.ComputeHeightRec r <| fun height_r ->
                (max height_l height_r) + 1u
                |> cont

    /// Returns the height of a AvlTree.
    static member private ComputeHeight (tree : AvlTree<'T>) =
        AvlTree.ComputeHeightRec tree id
        
    /// Determines if a AvlTree is correctly formed.
    /// It isn't necessary to call this at run-time, though it may be useful for asserting
    /// the correctness of functions which weren't extracted from the Isabelle/HOL theory.
    static member private AvlInvariant (tree : AvlTree<'T>) =
        match tree with
        | Empty -> true
        | Node (l, r, x, h) ->
            let height_l = AvlTree.ComputeHeight l
            let height_r = AvlTree.ComputeHeight r
            height_l = height_r
            || (height_l = (1u + height_r) || height_r = (1u + height_l))
            && h = ((max height_l height_r) + 1u)
            && AvlTree.AvlInvariant l
            && AvlTree.AvlInvariant r

    /// Returns the height of a AvlTree.
    static member inline Height (tree : AvlTree<'T>) =
        match tree with
        | Empty -> 0u
        | Node (_,_,_,h) -> h

    /// Determines if a AvlTree is empty.
    static member inline IsEmptyTree (tree : AvlTree<'T>) =
        match tree with
        | Empty -> true
        | Node (_,_,_,_) -> false

    /// Creates a AvlTree whose root node holds the specified value
    /// and the specified left and right subtrees.
    static member inline Create (value, l, r : AvlTree<'T>) =
        Node (l, r, value, (max (AvlTree.Height l) (AvlTree.Height r)) + 1u)

    /// Creates a AvlTree containing the specified value.
    static member Singleton value : AvlTree<'T> =
        AvlTree.Create (value, Empty, Empty)

    static member private mkt_bal_l (n, l, r : AvlTree<'T>) =
        if AvlTree.Height l = AvlTree.Height r + 2u then
            match l with
            | Empty ->
                failwith "mkt_bal_l"
            | Node (ll, lr, ln, _) ->
                if AvlTree.Height ll < AvlTree.Height lr then
                    match lr with
                    | Empty ->
                        failwith "mkt_bal_l"
                    | Node (lrl, lrr, lrn, _) ->
                        AvlTree.Create (lrn, AvlTree.Create (ln, ll, lrl), AvlTree.Create (n, lrr, r))
                else
                    AvlTree.Create (ln, ll, AvlTree.Create (n, lr, r))
        else
            AvlTree.Create (n, l, r)

    static member private mkt_bal_r (n, l, r : AvlTree<'T>) =
        if AvlTree.Height r = AvlTree.Height l + 2u then
            match r with
            | Empty ->
                failwith "mkt_bal_r"
            | Node (rl, rr, rn, _) ->
                if AvlTree.Height rr < AvlTree.Height rl then
                    match rl with
                    | Empty ->
                        failwith "mkt_bal_r"
                    | Node (rll, rlr, rln, _) ->
                        AvlTree.Create (rln, AvlTree.Create (n, l, rll), AvlTree.Create (rn, rlr, rr))
                else
                    AvlTree.Create (rn, AvlTree.Create (n, l, rl), rr)
        else
            AvlTree.Create (n, l, r)

    static member DeleteMax (tree : AvlTree<'T>) =
        match tree with
        | Empty ->
            invalidArg "tree" "Cannot delete the maximum value from an empty tree."
        | Node (l, Empty, n, _) ->
            n, l
        | Node (l, (Node (_,_,_,_) as right), n, _) ->
            let na, r = AvlTree.DeleteMax right
            na, AvlTree.mkt_bal_l (n, l, r)

    static member DeleteRoot (tree : AvlTree<'T>) =
        match tree with
        | Empty ->
            invalidArg "tree" "Cannot delete the root of an empty tree."
        | Node (Empty, r, _, _) -> r
        | Node ((Node (_,_,_,_) as left), Empty, _, _) ->
            left
        | Node ((Node (_,_,_,_) as left), (Node (_,_,_,_) as right), _, _) ->
            let new_n, l = AvlTree.DeleteMax left
            AvlTree.mkt_bal_r (new_n, l, right)

    /// Determines if a AvlTree contains a specified value.
    static member Contains (comparer : IComparer<'T>, tree : AvlTree<'T>, value : 'T) =
        match tree with
        | Empty ->
            false
        | Node (l, r, n, _) ->
            let comparison = comparer.Compare (value, n)
            if comparison = 0 then      // value = n
                true
            elif comparison < 0 then    // value < n
                AvlTree.Contains (comparer, l, value)
            else                        // value > n
                AvlTree.Contains (comparer, r, value)

    /// Removes the specified value from the tree.
    /// If the tree doesn't contain the value, no exception is thrown;
    /// the tree will be returned without modification.
    static member Delete (comparer : IComparer<'T>, tree : AvlTree<'T>, value : 'T) =
        match tree with
        | Empty ->
            Empty
        | Node (l, r, n, _) ->
            let comparison = comparer.Compare (value, n)
            if comparison = 0 then              // x = n
                AvlTree.DeleteRoot tree
            elif comparison < 0 then            // x < n
                let la = AvlTree.Delete (comparer, l, value)
                AvlTree.mkt_bal_r (n, la, r)
            else                                // x > n
                let a = AvlTree.Delete (comparer, r, value)
                AvlTree.mkt_bal_l (n, l, a)

    /// Adds a value to a AvlTree.
    /// If the tree already contains the value, no exception is thrown;
    /// the tree will be returned without modification.
    static member Insert (comparer : IComparer<'T>, tree : AvlTree<'T>, value : 'T) =
        match tree with
        | Empty ->
            Node (Empty, Empty, value, 1u)
        | Node (l, r, n, _) ->
            let comparison = comparer.Compare (value, n)
            if comparison = 0 then                              // x = n
                tree
            elif comparison < 0 then                            // x < n
                let l' = AvlTree.Insert (comparer, l, value)
                AvlTree.mkt_bal_l (n, l', r)
            else                                                // x > n
                let r' = AvlTree.Insert (comparer, r, value)
                AvlTree.mkt_bal_r (n, l, r')

    /// Gets the maximum (greatest) value stored in the AvlTree.
    static member MaxElement (tree : AvlTree<'T>) =
        match tree with
        | Empty ->
            invalidArg "tree" "The tree is empty."
        | Node (_, Empty, n, _) ->
            n
        | Node (_, right, _, _) ->
            AvlTree.MaxElement right

    /// Gets the minimum (least) value stored in the AvlTree.
    static member MinElement (tree : AvlTree<'T>) =
        match tree with
        | Empty ->
            invalidArg "tree" "The tree is empty."
        | Node (Empty, _, n, _) ->
            n
        | Node (left, _, _, _) ->
            AvlTree.MinElement left

    /// Extracts the minimum (least) value from a AvlTree,
    /// returning the value along with the updated tree.
    static member ExtractMin (tree : AvlTree<'T>) =
        match tree with
        | Empty ->
            invalidArg "tree" "The tree is empty."
        | Node (Empty, r, n, _) ->
            n, r
        | Node (Node (left, mid, a, _), r, x, _) ->
            // Rebalance the tree at the same time we're traversing downwards
            // to find the minimum value. This avoids the need to perform a
            // second traversal to remove the element once it's found.
            let n = AvlTree.Create (x, mid, r)
            AvlTree.Create (a, left, n)
            |> AvlTree.ExtractMin

    /// Extracts the minimum (least) value from a AvlTree,
    /// returning the value along with the updated tree.
    /// No exception is thrown if the tree is empty.
    static member TryExtractMin (tree : AvlTree<'T>) =
        // Is the tree empty?
        if AvlTree.IsEmptyTree tree then
            None, tree
        else
            let minElement, tree = AvlTree.ExtractMin tree
            Some minElement, tree

    /// Extracts the maximum (greatest) value from a AvlTree,
    /// returning the value along with the updated tree.
    static member ExtractMax (tree : AvlTree<'T>) =
        match tree with
        | Empty ->
            invalidArg "tree" "The tree is empty."
        | Node (l, Empty, n, _) ->
            n, l
        | Node (l, Node (mid, right, a, _), x, _) ->
            // Rebalance the tree at the same time we're traversing downwards
            // to find the maximum value. This avoids the need to perform a
            // second traversal to remove the element once it's found.
            let n = AvlTree.Create (x, l, mid)
            AvlTree.Create (a, n, right)
            |> AvlTree.ExtractMax

    /// Extracts the maximum (greatest) value from a AvlTree,
    /// returning the value along with the updated tree.
    /// No exception is thrown if the tree is empty.
    static member TryExtractMax (tree : AvlTree<'T>) =
        // Is the tree empty?
        if AvlTree.IsEmptyTree tree then
            None, tree
        else
            let maxElement, tree = AvlTree.ExtractMax tree
            Some maxElement, tree

    /// Counts the number of elements in the tree.
    static member Count (tree : AvlTree<'T>) =
        match tree with
        | Empty -> 0u
        | Node (Empty, Empty, _, _) -> 1u
        | Node (l, r, _, _) ->
            /// Mutable stack. Holds the trees which still need to be traversed.
            let stack = Stack (defaultStackCapacity)

            /// The number of elements discovered in the tree so far.
            let mutable count = 1u   // Start at one (1) to include this root node.

            // Traverse the tree using the mutable stack, incrementing the counter at each node.
            stack.Push r
            stack.Push l

            while stack.Count > 0 do
                match stack.Pop () with
                | Empty -> ()
                (* OPTIMIZATION: Handle a few of the cases specially here to avoid pushing empty
                   nodes on the stack. *)
                | Node (Empty, Empty, _, _) ->
                    // Increment the element count.
                    count <- count + 1u

                | Node (Empty, z, _, _)
                | Node (z, Empty, _, _) ->
                    // Increment the element count.
                    count <- count + 1u

                    // Push the non-empty child onto the stack.
                    stack.Push z

                | Node (l, r, _, _) ->
                    // Increment the element count.
                    count <- count + 1u

                    // Push the children onto the stack.
                    stack.Push r
                    stack.Push l

            // Return the element count.
            count

    //
    static member Iter (action : 'T -> unit) (tree : AvlTree<'T>) : unit =
        match tree with
        | Empty -> ()
        | Node (Empty, Empty, x, _) ->
            // Invoke the action with this single element.
            action x
        | Node (l, r, x, _) ->
            /// Mutable stack. Holds the trees which still need to be traversed.
            let stack = Stack (defaultStackCapacity)

            // Traverse the tree using the mutable stack, applying the folder function to
            // each value to update the state value.
            stack.Push r
            stack.Push <| AvlTree.Singleton x
            stack.Push l

            while stack.Count > 0 do
                match stack.Pop () with
                | Empty -> ()
                | Node (Empty, Empty, x, _) ->
                    // Apply this value to the action function.
                    action x

                | Node (Empty, z, x, _) ->
                    // Apply this value to the action function.
                    action x

                    // Push the non-empty child onto the stack.
                    stack.Push z

                | Node (l, r, x, _) ->
                    // Push the children onto the stack.
                    // Also push a new Node onto the stack which contains the value from
                    // this Node, so it'll be processed in the correct order.
                    stack.Push r
                    stack.Push <| AvlTree.Singleton x
                    stack.Push l

    /// Applies the given accumulating function to all elements in a AvlTree.
    static member Fold (folder : 'State -> 'T -> 'State) (state : 'State) (tree : AvlTree<'T>) =
        match tree with
        | Empty -> state
        | Node (Empty, Empty, x, _) ->
            // Invoke the folder function on this single element and return the result.
            folder state x
        | Node (l, r, x, _) ->
            // Adapt the folder function since we'll always supply all of the arguments at once.
            let folder = FSharpFunc<_,_,_>.Adapt folder

            /// Mutable stack. Holds the trees which still need to be traversed.
            let stack = Stack (defaultStackCapacity)

            /// The current state value.
            let mutable state = state

            // Traverse the tree using the mutable stack, applying the folder function to
            // each value to update the state value.
            stack.Push r
            stack.Push <| AvlTree.Singleton x
            stack.Push l

            while stack.Count > 0 do
                match stack.Pop () with
                | Empty -> ()
                | Node (Empty, Empty, x, _) ->
                    // Apply this value to the folder function.
                    state <- folder.Invoke (state, x)

                | Node (Empty, z, x, _) ->
                    // Apply this value to the folder function.
                    state <- folder.Invoke (state, x)

                    // Push the non-empty child onto the stack.
                    stack.Push z

                | Node (l, r, x, _) ->
                    // Push the children onto the stack.
                    // Also push a new Node onto the stack which contains the value from
                    // this Node, so it'll be processed in the correct order.
                    stack.Push r
                    stack.Push <| AvlTree.Singleton x
                    stack.Push l

            // Return the final state value.
            state

    /// Applies the given accumulating function to all elements in a AvlTree.
    static member FoldBack (folder : 'T -> 'State -> 'State) (state : 'State) (tree : AvlTree<'T>) =
        match tree with
        | Empty -> state
        | Node (Empty, Empty, x, _) ->
            // Invoke the folder function on this single element and return the result.
            folder x state
        | Node (l, r, x, _) ->
            // Adapt the folder function since we'll always supply all of the arguments at once.
            let folder = FSharpFunc<_,_,_>.Adapt folder

            /// Mutable stack. Holds the trees which still need to be traversed.
            let stack = Stack (defaultStackCapacity)

            /// The current state value.
            let mutable state = state

            // Traverse the tree using the mutable stack, applying the folder function to
            // each value to update the state value.
            stack.Push l
            stack.Push <| AvlTree.Singleton x
            stack.Push r

            while stack.Count > 0 do
                match stack.Pop () with
                | Empty -> ()
                | Node (Empty, Empty, x, _) ->
                    // Apply this value to the folder function.
                    state <- folder.Invoke (x, state)

                | Node (z, Empty, x, _) ->
                    // Apply this value to the folder function.
                    state <- folder.Invoke (x, state)

                    // Push the non-empty child onto the stack.
                    stack.Push z

                | Node (l, r, x, _) ->
                    // Push the children onto the stack.
                    // Also push a new Node onto the stack which contains the value from
                    // this Node, so it'll be processed in the correct order.
                    stack.Push l
                    stack.Push <| AvlTree.Singleton x
                    stack.Push r

            // Return the final state value.
            state

    /// Tests if any element of the collection satisfies the given predicate.
    static member Exists (predicate : 'T -> bool) (tree : AvlTree<'T>) : bool =
        match tree with
        | Empty -> false
        | Node (Empty, Empty, x, _) ->
            // Apply the predicate function to this element and return the result.
            predicate x
        | Node (l, r, x, _) ->
            /// Mutable stack. Holds the trees which still need to be traversed.
            let stack = Stack (defaultStackCapacity)

            /// Have we found a matching element?
            let mutable foundMatch = false

            // Traverse the tree using the mutable stack, applying the folder function to
            // each value to update the state value.
            stack.Push r
            stack.Push <| AvlTree.Singleton x
            stack.Push l

            while stack.Count > 0 && not foundMatch do
                match stack.Pop () with
                | Empty -> ()
                | Node (Empty, Empty, x, _) ->
                    // Apply the predicate to this element.
                    foundMatch <- predicate x

                | Node (Empty, z, x, _) ->
                    // Apply the predicate to this element.
                    foundMatch <- predicate x

                    // Push the non-empty child onto the stack.
                    stack.Push z

                | Node (l, r, x, _) ->
                    // Push the children onto the stack.
                    // Also push a new Node onto the stack which contains the value from
                    // this Node, so it'll be processed in the correct order.
                    stack.Push r
                    stack.Push <| AvlTree.Singleton x
                    stack.Push l

            // Return the value indicating whether or not a matching element was found.
            foundMatch

    /// Tests if all elements of the collection satisfy the given predicate.
    static member Forall (predicate : 'T -> bool) (tree : AvlTree<'T>) : bool =
        match tree with
        | Empty -> true
        | Node (Empty, Empty, x, _) ->
            // Apply the predicate function to this element and return the result.
            predicate x
        | Node (l, r, x, _) ->
            /// Mutable stack. Holds the trees which still need to be traversed.
            let stack = Stack (defaultStackCapacity)

            /// Have all of the elements we've seen so far matched the predicate?
            let mutable allElementsMatch = true

            // Traverse the tree using the mutable stack, applying the folder function to
            // each value to update the state value.
            stack.Push r
            stack.Push <| AvlTree.Singleton x
            stack.Push l

            while stack.Count > 0 && allElementsMatch do
                match stack.Pop () with
                | Empty -> ()
                | Node (Empty, Empty, x, _) ->
                    // Apply the predicate to this element.
                    allElementsMatch <- predicate x

                | Node (Empty, z, x, _) ->
                    // Apply the predicate to this element.
                    allElementsMatch <- predicate x

                    // Push the non-empty child onto the stack.
                    stack.Push z

                | Node (l, r, x, _) ->
                    // Push the children onto the stack.
                    // Also push a new Node onto the stack which contains the value from
                    // this Node, so it'll be processed in the correct order.
                    stack.Push r
                    stack.Push <| AvlTree.Singleton x
                    stack.Push l

            // Return the value indicating if all elements matched the predicate.
            allElementsMatch

    /// Builds a new AvlTree from the elements of a sequence.
    static member OfSeq (comparer : IComparer<'T>, sequence : seq<'T>) : AvlTree<'T> =
        (Empty, sequence)
        ||> Seq.fold (fun tree el ->
            AvlTree.Insert (comparer, tree, el))

    /// Builds a new AvlTree from the elements of an list.
    static member OfList (comparer : IComparer<'T>, list : 'T list) : AvlTree<'T> =
        (Empty, list)
        ||> List.fold (fun tree el ->
            AvlTree.Insert (comparer, tree, el))

    /// Builds a new AvlTree from the elements of an array.
    static member OfArray (comparer : IComparer<'T>, array : 'T[]) : AvlTree<'T> =
        (Empty, array)
        ||> Array.fold (fun tree el ->
            AvlTree.Insert (comparer, tree, el))

    (* NOTE : This works, but has been disabled for now because the existing F# Set
                implementation uses a custom IEnumerator implementation which has different
                characteristics; the unit tests expect to see these, so that implementation
                is used instead of this one (at least for now). *)
//    /// Returns a sequence containing the elements stored
//    /// in a AvlTree, ordered from least to greatest.
//    static member ToSeq (tree : AvlTree<'T>) =
//        seq {
//        match tree with
//        | Empty -> ()
//        | Node (l, r, n, _) ->
//            yield! AvlTree.ToSeq l
//            yield n
//            yield! AvlTree.ToSeq r
//        }

    /// Returns a list containing the elements stored in
    /// a AvlTree, ordered from least to greatest. 
    static member ToList (tree : AvlTree<'T>) =
        ([], tree)
        ||> AvlTree.FoldBack (fun el lst ->
            el :: lst)

    /// Returns an array containing the elements stored in
    /// a AvlTree, ordered from least to greatest.
    static member ToArray (tree : AvlTree<'T>) =
        let elements = ResizeArray ()
        AvlTree.Iter elements.Add tree
        elements.ToArray ()

    //
    // TODO : This could be replaced by 'mkt_bal_l' and 'mkt_bal_r'.
    static member Rebalance (t1, t2, k) : AvlTree<'T> =
        let t1h = AvlTree.Height t1
        let t2h = AvlTree.Height t2
        if t2h > t1h + 2u then // right is heavier than left
            match t2 with
            | Node (t2l, t2r, t2k, _) ->
                // one of the nodes must have height > height t1 + 1
                if AvlTree.Height t2l > t1h + 1u then  // balance left: combination
                    match t2l with
                    | Node (t2ll, t2lr, t2lk, _) ->
                        AvlTree.Create (
                            t2lk,
                            AvlTree.Create (k, t1, t2ll),
                            AvlTree.Create (t2k, t2lr, t2r))
                    | _ -> failwith "rebalance"
                else // rotate left
                    AvlTree.Create (
                        t2k,
                        AvlTree.Create (k, t1, t2l),
                        t2r)
            | _ -> failwith "rebalance"

        elif t1h > t2h + 2u then // left is heavier than right
            match t1 with
            | Node (t1l, t1r, t1k, _) ->
                // one of the nodes must have height > height t2 + 1
                if AvlTree.Height t1r > t2h + 1u then
                    // balance right: combination
                    match t1r with
                    | Node (t1rl, t1rr, t1rk, _) ->
                        AvlTree.Create (
                            t1rk,
                            AvlTree.Create (t1k, t1l, t1rl),
                            AvlTree.Create (k, t1rr, t2))
                    | _ -> failwith "rebalance"
                else
                    AvlTree.Create (
                        t1k,
                        t1l,
                        AvlTree.Create (k, t1r, t2))
            | _ -> failwith "rebalance"

        else
            AvlTree.Create (k, t1, t2)

    //
    static member Balance (comparer : IComparer<'T>, t1, t2, k) =
        // Given t1 < k < t2 where t1 and t2 are "balanced",
        // return a balanced tree for <t1,k,t2>.
        // Recall: balance means subtrees heights differ by at most "tolerance"
        match t1, t2 with
        // TODO : The first two patterns can be merged to use the same handler.
        | Empty, t2 ->
            // drop t1 = empty
            AvlTree.Insert (comparer, t2, k)
        | t1, Empty ->
            // drop t2 = empty
            AvlTree.Insert (comparer, t1, k)

        // TODO : The next two patterns can be merged to use the same handler.
        | Node (Empty, Empty, k1, _), t2 ->
            let t' = AvlTree.Insert (comparer, t2, k1)
            AvlTree.Insert (comparer, t', k)
        | t1, Node (Empty, Empty, k2, _) ->
            let t' = AvlTree.Insert (comparer, t1, k2)
            AvlTree.Insert (comparer, t', k)

        | Node (t11, t12, k1, h1), Node (t21, t22, k2, h2) ->
            // Have:  (t11 < k1 < t12) < k < (t21 < k2 < t22)
            // Either (a) h1,h2 differ by at most 2 - no rebalance needed.
            //        (b) h1 too small, i.e. h1+2 < h2
            //        (c) h2 too small, i.e. h2+2 < h1
            if   h1+2u < h2 then
                // case: b, h1 too small
                // push t1 into low side of t2, may increase height by 1 so rebalance
                AvlTree.Rebalance (AvlTree.Balance (comparer, t1, t21, k), t22, k2)
            elif h2+2u < h1 then
                // case: c, h2 too small
                // push t2 into high side of t1, may increase height by 1 so rebalance
                AvlTree.Rebalance (t11, AvlTree.Balance (comparer, t12, t2, k), k1)
            else
                // case: a, h1 and h2 meet balance requirement
                AvlTree.Create (k, t1, t2)

    //
    static member Split (comparer : IComparer<'T>, t, pivot) : AvlTree<'T> * bool * AvlTree<'T> =
        // Given a pivot and a set t
        // Return { x in t s.t. x < pivot }, pivot in t? , { x in t s.t. x > pivot }
        match t with
        | Empty  ->
            Empty, false, Empty
        | Node (Empty, Empty, k1, _) ->
            let c = comparer.Compare (k1, pivot)
            if   c < 0 then t    ,false,Empty // singleton under pivot
            elif c = 0 then Empty,true ,Empty // singleton is    pivot
            else            Empty,false,t     // singleton over  pivot
        | Node (t11, t12, k1, _) ->
            let c = comparer.Compare (pivot, k1)
            if   c < 0 then // pivot t1
                let t11Lo, havePivot, t11Hi = AvlTree.Split (comparer, t11, pivot)
                t11Lo, havePivot, AvlTree.Balance (comparer, t11Hi, t12, k1)
            elif c = 0 then // pivot is k1
                t11,true,t12
            else            // pivot t2
                let t12Lo, havePivot, t12Hi = AvlTree.Split (comparer, t12, pivot)
                AvlTree.Balance (comparer, t11, t12Lo, k1), havePivot, t12Hi

    /// Computes the union of two AvlTrees.
    static member Union (comparer : IComparer<'T>, t1 : AvlTree<'T>, t2 : AvlTree<'T>) : AvlTree<'T> =
        // Perf: tried bruteForce for low heights, but nothing significant 
        match t1, t2 with
        | Empty, t -> t
        | t, Empty -> t
        | Node (Empty, Empty, k1, _), t2 ->
            AvlTree.Insert (comparer, t2, k1)
        | t1, Node (Empty, Empty, k2, _) ->
            AvlTree.Insert (comparer, t1, k2)

        | Node (t11, t12, k1, h1), Node (t21, t22, k2, h2) -> // (t11 < k < t12) AND (t21 < k2 < t22) 
            // Divide and Quonquer:
            //   Suppose t1 is largest.
            //   Split t2 using pivot k1 into lo and hi.
            //   Union disjoint subproblems and then combine. 
            if h1 > h2 then
                let lo, _, hi = AvlTree.Split (comparer, t2, k1)
                AvlTree.Balance (
                    comparer,
                    AvlTree.Union (comparer, t11, lo),
                    AvlTree.Union (comparer, t12, hi),
                    k1)
            else
                let lo, _, hi = AvlTree.Split (comparer, t1, k2)
                AvlTree.Balance (
                    comparer,
                    AvlTree.Union (comparer, t21, lo),
                    AvlTree.Union (comparer, t22, hi),
                    k2)

    /// Implementation. Computes the intersection of two AvlTrees.
    static member private IntersectionAux (comparer : IComparer<'T>, b, m, acc) : AvlTree<'T> =
        match m with
        | Empty -> acc
        | Node (Empty, Empty, k, _) ->
            if AvlTree.Contains (comparer, b, k) then
                AvlTree.Insert (comparer, acc, k)
            else acc
        | Node (l, r, k, _) ->
            let acc =
                let acc = AvlTree.IntersectionAux (comparer, b, r, acc)
                if AvlTree.Contains (comparer, b, k) then
                    AvlTree.Insert (comparer, acc, k)
                else acc 
            AvlTree.IntersectionAux (comparer, b, l, acc)

    /// Computes the intersection of two AvlTrees.
    static member Intersection (comparer : IComparer<'T>, tree1 : AvlTree<'T>, tree2 : AvlTree<'T>) : AvlTree<'T> =
        AvlTree.IntersectionAux (comparer, tree2, tree1, Empty)

    /// Returns a new AvlTree created by removing the elements of the
    /// second AvlTree from the first.
    static member Difference (comparer : IComparer<'T>, tree1 : AvlTree<'T>, tree2 : AvlTree<'T>) : AvlTree<'T> =
        (* OPTIMIZE :   This function should be re-implemented to use the linear-time
                        algorithm which traverses both trees simultaneously and merges
                        them in a single pass. *)

        // Fold over tree2, removing it's elements from tree1
        (tree1, tree2)
        ||> AvlTree.Fold (fun tree el ->
            AvlTree.Delete (comparer, tree, el))

    //
    static member IsSubset (comparer : IComparer<'T>, set1 : AvlTree<'T>, set2 : AvlTree<'T>) : bool =
        AvlTree.Forall (fun x -> AvlTree.Contains (comparer, set2, x)) set1

    //
    static member IsProperSubset (comparer : IComparer<'T>, set1 : AvlTree<'T>, set2 : AvlTree<'T>) : bool =
        AvlTree.Forall (fun x -> AvlTree.Contains (comparer, set2, x)) set1
        && AvlTree.Exists (fun x -> not (AvlTree.Contains (comparer, set1, x))) set2

    static member private CompareStacks (comparer : IComparer<'T>, l1 : AvlTree<'T> list, l2 : AvlTree<'T> list) : int =
        match l1, l2 with
        | [], [] -> 0
        | [], _ -> -1
        | _, [] -> 1
        | (Empty :: t1), (Empty :: t2) ->
            AvlTree.CompareStacks (comparer, t1, t2)
        | (Node (Empty, Empty, n1k, _) :: t1), (Node (Empty, Empty, n2k, _) :: t2) ->
            match comparer.Compare (n1k, n2k) with
            | 0 ->
                AvlTree.CompareStacks (comparer, t1, t2)
            | c -> c

        | (Node (Empty, Empty, n1k, _) :: t1), (Node (Empty, n2r, n2k, _) :: t2) ->
            match comparer.Compare (n1k, n2k) with
            | 0 ->
                AvlTree.CompareStacks (comparer, Empty :: t1, n2r :: t2)
            | c -> c

        | (Node (Empty, n1r, n1k, _) :: t1), (Node (Empty, Empty, n2k, _) :: t2) ->
            match comparer.Compare (n1k, n2k) with
            | 0 ->
                AvlTree.CompareStacks (comparer, n1r :: t1, Empty :: t2)
            | c -> c

        | (Node (Empty, n1r, n1k, _) :: t1), (Node (Empty, n2r, n2k, _) :: t2) ->
            match comparer.Compare (n1k, n2k) with
            | 0 ->
                AvlTree.CompareStacks (comparer, n1r :: t1, n2r :: t2)
            | c -> c

        | ((Node (Empty, Empty, n1k, _) :: t1) as l1), _ ->
            AvlTree.CompareStacks (comparer, Empty :: l1, l2)
        
        | (Node (n1l, n1r, n1k, _) :: t1), _ ->
            AvlTree.CompareStacks (comparer, n1l :: Node (Empty, n1r, n1k, 0u) :: t1, l2)
        
        | _, ((Node (Empty, Empty, n2k, _) :: t2) as l2) ->
            AvlTree.CompareStacks (comparer, l1, Empty :: l2)
        
        | _, (Node (n2l, n2r, n2k, _) :: t2) ->
            AvlTree.CompareStacks (comparer, l1, n2l :: Node (Empty, n2r, n2k, 0u) :: t2)
                
    static member Compare (comparer : IComparer<'T>, s1 : AvlTree<'T>, s2 : AvlTree<'T>) : int =
        match s1, s2 with
        | Empty, Empty -> 0
        | Empty, _ -> -1
        | _, Empty -> 1
        | _ ->
            AvlTree<'T>.CompareStacks (comparer, [s1], [s2])

    //
    static member private myadd comparer left x (right : AvlTree<'T>) =
        match right with
        | Empty ->
            Node (Empty, Empty, x, 1u)
        | Node (l, r, vx, _) ->
            if left then
                AvlTree.Balance (comparer, AvlTree.myadd comparer left x l, r, vx)
            else
                AvlTree.Balance (comparer, l, AvlTree.myadd comparer left x r, vx)

    /// Join two trees together at a pivot point.
    /// The resulting tree may be unbalanced.
    static member Join comparer (v : 'T) l (r : AvlTree<'T>) =
        match l, r with
        | Empty, _ ->
            AvlTree.myadd comparer true v r
        | _, Empty ->
            AvlTree.myadd comparer false v l
        | Node (ll, lr, lx, lh), Node (rl, rr, rx, rh) ->
            if lh > rh + 2u then
                AvlTree.Balance (comparer, ll, AvlTree.Join comparer v lr r, lx)
            else if rh > lh + 2u then
                AvlTree.Balance (comparer, AvlTree.Join comparer v l rl, rr, rx)
            else
                AvlTree.Create (v, l, r)

    /// Reroot of balanced trees.
    static member Reroot comparer l (r : AvlTree<'T>) =
        if AvlTree.Height l > AvlTree.Height r then
            let i, l' = AvlTree.ExtractMax l
            AvlTree.Join comparer i l' r
        else
            match r with
            | Empty -> Empty
            | Node (_,_,_,_) ->
                let i, r' = AvlTree.ExtractMin r
                AvlTree.Join comparer i l r'



/// A Discrete Interval Encoding Tree (DIET) specialized to the 'char' type.
/// This is abbreviated in our documentation as a 'char-DIET'.
type private CharDiet = AvlTree<char * char>

/// Functional operations for char-DIETs.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module private Diet =
    open System.Collections.Generic
    open LanguagePrimitives

    //
    let inline private pred (c : char) : char =
        char (int c - 1)
    
    //
    let inline private succ (c : char) : char =
        char (int c + 1)

    //
    let inline private dist (x : char) (y : char) : int =
        int y - int x

    //
    let inline private safe_pred (limit : char) (c : char) =
        if limit < c then
            char (int c - 1)
        else c

    //
    let inline private safe_succ (limit : char) (c : char) =
        if limit > c then
            char (int c + 1)
        else c

    //
    let inline height (t : CharDiet) =
        AvlTree.Height t

    /// Character interval comparer.
    let private comparer = LanguagePrimitives.FastGenericComparer<char * char>

    //
    let rec private find_del_left p (tree : CharDiet) =
        match tree with
        | Empty ->
            p, Empty
        | Node (left, right, (x, y), _) ->
            if p > succ y then
                let p', right' = find_del_left p right
                p', AvlTree.Join comparer (x, y) left right'
            elif p < x then
                find_del_left p left
            else
                x, left

    //
    let rec private find_del_right p (tree : CharDiet) =
        match tree with
        | Empty ->
            p, Empty
        | Node (left, right, (x, y), _) ->
            if p < pred x then
                let p', left' = find_del_right p left
                p', AvlTree.Join comparer (x, y) left' right
            elif p > y then
                find_del_right p right
            else
                y, right

    /// An empty DIET.
    let empty : CharDiet =
        AvlTree.Empty

    /// Determines if a DIET is empty.
    let inline isEmpty (tree : CharDiet) =
        AvlTree.IsEmptyTree tree

    /// Determines if a DIET contains a specified value.
    let rec contains value (tree : CharDiet) =
        match tree with
        | Empty ->
            false
        | Node (left, right, (x, y), _) ->
            if value < x then
                contains value left
            elif value > y then
                contains value right
            else true
        
    /// Gets the maximum (greatest) value stored in the DIET.
    let maxElement (tree : CharDiet) : char =
        match tree with
        | Empty ->
            invalidArg "tree" "The tree is empty."
        | tree ->
            snd <| AvlTree.MaxElement tree
    
    /// Gets the minimum (least) value stored in the DIET.
    let minElement (tree : CharDiet) : char =
        match tree with
        | Empty ->
            invalidArg "tree" "The tree is empty."
        | tree ->
            fst <| AvlTree.MinElement tree

    /// Gets the minimum (least) and maximum (greatest) values store in the DIET.
    let bounds (tree : CharDiet) : char * char =
        match tree with
        | Empty ->
            invalidArg "tree" "The tree is empty."
        | tree ->
            minElement tree, maxElement tree

    /// Creates a DIET containing the specified value.
    let singleton value : CharDiet =
        AvlTree.Singleton (value, value)

    /// Creates a DIET containing the specified range of values.
    let ofRange minValue maxValue : CharDiet =
        // For compatibility with the F# range operator,
        // when minValue > minValue it's just considered
        // to be an empty range.
        if minValue >= maxValue then
            empty
        else
            AvlTree.Singleton (minValue, maxValue)

    /// Returns the number of elements in the set.
    let count (t : CharDiet) =
        // OPTIMIZE : Modify this to use a mutable stack instead of an F# list.
        let rec cardinal_aux acc = function
            | [] -> acc
            | Empty :: ts ->
                cardinal_aux acc ts
            | Node (left, right, (x, y), _) :: ts ->
                let d = dist x y
                cardinal_aux (acc + d + 1) (left :: right :: ts)
        
        cardinal_aux 0 [t]

    /// Returns the number of intervals in the set.
    let intervalCount (t : CharDiet) =
        // OPTIMIZE : Modify this to use a mutable stack instead of an F# list.
        let rec cardinal_aux acc = function
            | [] -> acc
            | Empty :: ts ->
                cardinal_aux acc ts
            | Node (left, right, _, _) :: ts ->
                cardinal_aux (acc + 1) (left :: right :: ts)
        
        cardinal_aux 0 [t]

    /// Returns a new set with the specified value added to the set.
    /// No exception is thrown if the set already contains the value.
    let rec add value (tree : CharDiet) : CharDiet =
        match tree with
        | Empty ->
            AvlTree.Singleton (value, value)
        | Node (left, right, (x, y), h) as tree ->
            if value >= x then
                if value <= y then tree
                elif value > succ y then
                    AvlTree.Join comparer (x, y) left (add value right)
                elif AvlTree.IsEmptyTree right then
                    Node (left, right, (x, value), h)
                else
                    let (u, v), r = AvlTree.ExtractMin right
                    if pred u = value then
                        AvlTree.Join comparer (x, v) left r
                    else
                        Node (left, right, (x, value), h)

            elif value < pred x then
                AvlTree.Join comparer (x, y) (add value left) right
            elif AvlTree.IsEmptyTree left then
                Node (left, right, (value, y), h)
            else
                let (u, v), l = AvlTree.ExtractMax left
                if succ v = value then
                    AvlTree.Join comparer (u, y) l right
                else
                    Node (left, right, (value, y), h)

    /// Returns a new set with the specified range of values added to the set.
    /// No exception is thrown if any of the values are already contained in the set.
    let rec addRange (p, q) (tree : CharDiet) : CharDiet =
        match tree with
        | Empty ->
            AvlTree.Singleton (p, q)
        | Node (left, right, (x, y), _) ->
            if q < pred x then
                AvlTree.Join comparer (x, y) (addRange (p, q) left) right
            elif p > succ y then
                AvlTree.Join comparer (x, y) left (addRange (p, q) right)
            else
                let x', left' =
                    if p >= x then x, left
                    else find_del_left p left
                let y', right' =
                    if q <= y then y, right
                    else find_del_right q right

                AvlTree.Join comparer (x', y') left' right'

    /// Returns a new set with the given element removed.
    /// No exception is thrown if the set doesn't contain the specified element.
    let rec remove value (tree : CharDiet) : CharDiet =
        match tree with
        | Empty ->
            Empty
        | Node (left, right, (x, y), h) ->
            let czx = compare value x
            if czx < 0 then
                AvlTree.Join comparer (x, y) (remove value left) right
            else
                let cyz = compare y value
                if cyz < 0 then
                    AvlTree.Join comparer (x, y) left (remove value right)
                elif cyz = 0 then
                    if czx = 0 then
                        AvlTree.Reroot comparer left right
                    else
                        Node (left, right, (x, pred y), h)
                elif czx = 0 then
                    Node (left, right, (succ x, y), h)
                else
                    addRange (succ value, y) (Node (left, right, (x, pred value), h))

    /// Helper function for computing the union of two sets.
    let rec private union' (input : CharDiet) limit head (stream : CharDiet)
        : CharDiet * (char * char) option * CharDiet =
        match head with
        | None ->
            input, None, Empty
        | Some (x, y) ->
            match input with
            | Empty ->
                Empty, head, stream
            | Node (left, right, (a, b), _) ->
                let left', head, stream =
                    if x < a then
                        union' left (Some <| pred a) head stream
                    else
                        left, head, stream
                union_helper left' (a, b) right limit head stream

    /// Helper function for computing the union of two sets.
    and private union_helper left (a, b) (right : CharDiet) limit head stream =
        match head with
        | None ->
            AvlTree.Join comparer (a, b) left right, None, Empty
        | Some (x, y) ->
            let greater_limit z =
                match limit with
                | None -> false
                | Some u ->
                    z >= u

            if y < a && y < pred a then
                let left' = addRange (x, y) left
                let head, stream = AvlTree.TryExtractMin stream
                union_helper left' (a, b) right limit head stream

            elif x > b && x > succ b then
                let right', head, stream = union' right limit head stream
                AvlTree.Join comparer (a, b) left right', head, stream

            elif b >= y then
                let head, stream = AvlTree.TryExtractMin stream
                union_helper left (min a x, b) right limit head stream

            elif greater_limit y then
                left, Some (min a x, y), stream

            else
                let right', head, stream = union' right limit (Some (min a x, y)) stream
                AvlTree.Reroot comparer left right', head, stream

    /// Computes the union of the two sets.
    let rec union (input : CharDiet) (stream : CharDiet) : CharDiet =
        if AvlTree.Height stream > AvlTree.Height input then
            union stream input
        else
            #if DEBUG
            let inputCount = count input
            let streamCount = count stream
            /// The minimum possible number of elements in the resulting set.
            let minPossibleResultCount =
                max inputCount streamCount
            /// The maximum possible number of elements in the resulting set.
            let maxPossibleResultCount =
                inputCount + streamCount
            #endif

            let result =
                let result, head', stream'' =
                    let head, stream' = AvlTree.TryExtractMin stream
                    union' input None head stream'

                match head' with
                | None ->
                    result
                | Some i ->
                    AvlTree.Join comparer i result stream''

            #if DEBUG
            let resultCount = count result
//            let inputArr =
//                if resultCount >= minPossibleResultCount then Array.empty
//                else toArray input
//            let streamArr =
//                if resultCount >= minPossibleResultCount then Array.empty
//                else toArray stream
                    
            Debug.Assert (
                resultCount >= minPossibleResultCount,
                sprintf "The result set should not contain fewer than %i elements, but it contains only %i elements."
                    minPossibleResultCount resultCount)
            Debug.Assert (
                resultCount <= maxPossibleResultCount,
                sprintf "The result set should not contain more than %i elements, but it contains %i elements."
                    maxPossibleResultCount resultCount)
            #endif
            result

    /// Helper function for computing the intersection of two sets.
    let rec private inter' (input : CharDiet) head (stream : CharDiet) : CharDiet * (char * char) option * CharDiet =
        match head with
        | None ->
            Empty, None, Empty
        | Some (x, y) ->
            match input with
            | Empty ->
                Empty, head, stream
            | Node (left, right, (a, b), _) ->
                let left, head, stream =
                    if x < a then
                        inter' left head stream
                    else
                        Empty, head, stream

                inter_helper (a, b) right left head stream

    /// Helper function for computing the intersection of two sets.
    and private inter_helper (a, b) (right : CharDiet) (left : CharDiet) head stream =
        match head with
        | None ->
            left, None, Empty
        | Some (x, y) ->
            if y < a then
                if AvlTree.IsEmptyTree stream then
                    (left, None, Empty)
                else
                    let head, stream = AvlTree.ExtractMin stream
                    inter_helper (a, b) right left (Some head) stream
            elif b < x then
                let right, head, stream = inter' right head stream
                AvlTree.Reroot comparer left right, head, stream
            elif y >= safe_pred y b then
                let right, head, stream = inter' right head stream
                (AvlTree.Join comparer (max x a, min y b) left right), head, stream
            else
                let left = addRange (max x a, y) left
                inter_helper (succ y, b) right left head stream

    /// Computes the intersection of the two sets.
    let rec intersect (input : CharDiet) (stream : CharDiet) : CharDiet =
        if AvlTree.Height stream > AvlTree.Height input then
            intersect stream input
        elif AvlTree.IsEmptyTree stream then
            Empty
        else
            #if DEBUG
            let inputCount = count input
            let streamCount = count stream
            /// The minimum possible number of elements in the resulting set.
            let minPossibleResultCount =
                min inputCount streamCount
            /// The maximum possible number of elements in the resulting set.
            let maxPossibleResultCount =
                inputCount + streamCount
            #endif

            let result, _, _ =
                let head, stream = AvlTree.ExtractMin stream
                inter' input (Some head) stream

            #if DEBUG
            let resultCount = count result
//            let inputArr =
//                if resultCount >= minPossibleResultCount then Array.empty
//                else toArray input
//            let streamArr =
//                if resultCount >= minPossibleResultCount then Array.empty
//                else toArray stream
                    
            Debug.Assert (
                resultCount >= minPossibleResultCount,
                sprintf "The result set should not contain fewer than %i elements, but it contains only %i elements."
                    minPossibleResultCount resultCount)
            Debug.Assert (
                resultCount <= maxPossibleResultCount,
                sprintf "The result set should not contain more than %i elements, but it contains %i elements."
                    maxPossibleResultCount resultCount)
            #endif
            result

    /// Helper function for computing the difference of two sets.
    let rec private diff' (input : CharDiet) head (stream : CharDiet) : CharDiet * (char * char) option * CharDiet =
        match head with
        | None ->
            input, None, Empty
        | Some (x, y) ->
            match input with
            | Empty ->
                Empty, head, stream
            | Node (left, right, (a, b), _) ->
                let left, head, stream =
                    if x < a then
                        diff' left head stream
                    else
                        left, head, stream
                diff_helper (a, b) right left head stream

    /// Helper function for computing the difference of two sets.
    and private diff_helper (a, b) (right : CharDiet) (left : CharDiet) head stream =
        match head with
        | None ->
            AvlTree.Join comparer (a, b) left right, None, Empty
        | Some (x, y) ->
            if y < a then
                // [x, y] and [a, b] are disjoint
                let head, stream = AvlTree.TryExtractMin stream
                diff_helper (a, b) right left head stream
            elif b < x then
                // [a, b] and [x, y] are disjoint
                let right, head, stream = diff' right head stream
                AvlTree.Join comparer (a, b) left right, head, stream
            elif a < x then
                // [a, b] and [x, y] overlap
                // a < x
                diff_helper (x, b) right ((addRange (a, pred x) left)) head stream
            elif y < b then
                // [a, b] and [x, y] overlap
                // y < b
                let head, stream = AvlTree.TryExtractMin stream
                diff_helper (succ y, b) right left head stream
            else
                // [a, b] and [x, y] overlap
                let right, head, stream = diff' right head stream
                AvlTree.Reroot comparer left right, head, stream

    /// Returns a new set with the elements of the second set removed from the first.
    let difference (input : CharDiet) (stream : CharDiet) : CharDiet =
        if AvlTree.IsEmptyTree stream then
            input
        else
            #if DEBUG
            /// The minimum possible number of elements in the resulting set.
            let minPossibleResultCount =
                let inputCount = count input
                let streamCount = count stream
                GenericMaximum 0 (inputCount - streamCount)
            #endif

            let head, stream' = AvlTree.ExtractMin stream
            let result, _, _ = diff' input (Some head) stream'

            #if DEBUG
            let resultCount = count result
            Debug.Assert (
                resultCount >= minPossibleResultCount,
                sprintf "The result set should not contain fewer than %i elements, but it contains only %i elements."
                    minPossibleResultCount resultCount)
            #endif

            result

    /// Comparison function for DIETs.
    let rec comparison (t1 : CharDiet) (t2 : CharDiet) =
        match t1, t2 with
        | Node (_,_,_,_), Node (_,_,_,_) ->
            let (ix1, iy1), r1 = AvlTree.ExtractMin t1
            let (ix2, iy2), r2 = AvlTree.ExtractMin t2
            let c =
                let d = compare ix1 ix2
                if d <> 0 then -d
                else compare iy1 iy2
            if c <> 0 then c
            else comparison r1 r2
        
        | Node (_,_,_,_), Empty -> 1
        | Empty, Empty -> 0
        | Empty, Node (_,_,_,_) -> -1

    /// Equality function for DIETs.
    let equal (t1 : CharDiet) (t2 : CharDiet) =
        comparison t1 t2 = 0

    //
    let rec split x (tree : CharDiet) : CharDiet * bool * CharDiet =
        match tree with
        | Empty ->
            Empty, false, Empty
        | Node (l, r, (a, b), _) ->
            let cxa = compare x a
            if cxa < 0 then
                let ll, pres, rl = split x l
                ll, pres, AvlTree.Join comparer (a, b) rl r
            else
                let cbx = compare b x
                if cbx < 0 then
                    let lr, pres, rr = split x r
                    AvlTree.Join comparer (a, b) l lr, pres, rr
                else
                    (if cxa = 0 then l else addRange (a, pred x) l),
                    true,
                    (if cbx = 0 then r else addRange (succ x, b) r)

    /// Applies the given accumulating function to all elements in a DIET.
    let fold (folder : 'State -> char -> 'State) (state : 'State) (tree : CharDiet) =
        // Preconditions
        // NONE -- Skip null check because the Empty tree is represented as null.

        let folder = FSharpFunc<_,_,_>.Adapt folder

        let rangeFolder (state : 'State) (lo, hi) =
            // Fold over the items in increasing order.
            let mutable state = state
            for x = int lo to int hi do
                state <- folder.Invoke (state, char x)
            state

        AvlTree.Fold rangeFolder state tree

    /// Applies the given accumulating function to all elements in a DIET.
    let foldBack (folder : char -> 'State -> 'State) (tree : CharDiet) (state : 'State) =
        // Preconditions
        // NONE -- Skip null check because the Empty tree is represented as null.

        let folder = FSharpFunc<_,_,_>.Adapt folder

        let rangeFolder (lo, hi) (state : 'State) =
            // Fold over the items in decreasing order.
            let mutable state = state
            for x = int hi downto int lo do
                state <- folder.Invoke (char x, state)
            state

        AvlTree.FoldBack rangeFolder state tree

    /// Applies the given function to all elements in a DIET.
    let iter (action : char -> unit) (tree : CharDiet) =
        // Preconditions
        // NONE -- Skip null check because the Empty tree is represented as null.

        /// Applies the action to all values within an interval.
        let intervalApplicator (lo, hi) =
            for x = int lo to int hi do
                action (char x)

        AvlTree.Iter intervalApplicator tree

    //
    let forall (predicate : char -> bool) (t : CharDiet) =
        // OPTIMIZE : Rewrite this to short-circuit and return early
        // if we find a non-matching element.
        (true, t)
        ||> fold (fun state el ->
            state && predicate el)

    //
    let exists (predicate : char -> bool) (t : CharDiet) =
        // OPTIMIZE : Rewrite this to short-circuit and return early
        // if we find a non-matching element.
        (false, t)
        ||> fold (fun state el ->
            state || predicate el)

    //
    let rec toSeq (tree : CharDiet) =
        seq {
        match tree with
        | Empty -> ()
        | Node (l, r, (x, y), _) ->
            yield! toSeq l
            yield! seq {x .. y}
            yield! toSeq r
        }

    //
    let toList (tree : CharDiet) =
        ([], tree)
        ||> fold (fun list el ->
            el :: list)

    //
    let toArray (tree : CharDiet) =
        let elements = ResizeArray ()
        iter elements.Add tree
        elements.ToArray ()

    //
    let toSet (tree : CharDiet) =
        (Set.empty, tree)
        ||> fold (fun set el ->
            Set.add el set)

    /// Builds a new DIET from the elements of a sequence.
    let ofSeq (sequence : seq<_>) : CharDiet =
        (Empty, sequence)
        ||> Seq.fold (fun tree el ->
            add el tree)

    /// Builds a new DIET from the elements of an F# list.
    let ofList (list : _ list) : CharDiet =
        (Empty, list)
        ||> List.fold (fun tree el ->
            add el tree)

    /// Builds a new DIET from the elements of an array.
    let ofArray (array : _[]) : CharDiet =
        (Empty, array)
        ||> Array.fold (fun tree el ->
            add el tree)

    /// Builds a new DIET from an F# Set.
    let ofSet (set : Set<_>) : CharDiet =
        (Empty, set)
        ||> Set.fold (fun tree el ->
            add el tree)


/// Character set implementation based on a Discrete Interval Encoding Tree.
/// This is faster and more efficient than the built-in F# Set<'T>,
/// especially for dense sets.
[<DebuggerDisplay("Count = {Count}, Intervals = {IntervalCount}")>]
type CharSet private (dietSet : CharDiet) =
    //
    static let empty = CharSet (Diet.empty)

    //
    static member Empty
        with get () = empty
    
    override __.GetHashCode () =
        // TODO : Come up with a better hashcode function.
        (Diet.count dietSet) * (int <| AvlTree.Height dietSet)
    
    override __.Equals other =
        match other with
        | :? CharSet as other ->
            Diet.equal dietSet other.DietSet
        | _ ->
            false

    //
    member private __.DietSet
        with get () = dietSet

    //
    member __.Count
        with get () =
            Diet.count dietSet

    //
    member __.IntervalCount
        with get () =
            Diet.intervalCount dietSet

    //
    member __.MaxElement
        with get () =
            Diet.maxElement dietSet

    //
    member __.MinElement
        with get () =
            Diet.minElement dietSet

    /// The set containing the given element.
    static member FromElement value =
        CharSet (Diet.singleton value)

    /// The set containing the elements in the given range.
    static member FromRange (lowerBound, upperBound) =
        CharSet (Diet.ofRange lowerBound upperBound)

    //
    static member IsEmpty (charSet : CharSet) =
        Diet.isEmpty charSet.DietSet

    /// Returns a new set with an element added to the set.
    /// No exception is raised if the set already contains the given element.
    static member Add (value, charSet : CharSet) =
        CharSet (Diet.add value charSet.DietSet)

    //
    static member AddRange (lower, upper, charSet : CharSet) =
        CharSet (Diet.addRange (lower, upper) charSet.DietSet)

    //
    static member Remove (value, charSet : CharSet) =
        CharSet (Diet.remove value charSet.DietSet)

    //
    static member Contains (value, charSet : CharSet) =
        Diet.contains value charSet.DietSet

//    //
//    static member ToList (charSet : CharSet) =
//        Diet.toList charSet.DietSet

    //
    static member OfList list =
        CharSet (Diet.ofList list)

//    //
//    static member ToSet (charSet : CharSet) =
//        Diet.toSet charSet.DietSet

//    //
//    static member OfSet set =
//        CharSet (Diet.ofSet set)

//    //
//    static member ToArray (charSet : CharSet) =
//        Diet.toArray charSet.DietSet
    
    //
    static member OfArray array =
        CharSet (Diet.ofArray array)

//    //
//    static member ToSequence (charSet : CharSet) =
//        Diet.toSeq charSet.DietSet

    //
    static member OfSequence sequence =
        CharSet (Diet.ofSeq sequence)

    //
    static member Difference (charSet1 : CharSet, charSet2 : CharSet) =
        CharSet (Diet.difference charSet1.DietSet charSet2.DietSet)

    //
    static member Intersect (charSet1 : CharSet, charSet2 : CharSet) =
        CharSet (Diet.intersect charSet1.DietSet charSet2.DietSet)

    //
    static member Union (charSet1 : CharSet, charSet2 : CharSet) =
        CharSet (Diet.union charSet1.DietSet charSet2.DietSet)

    //
    static member Fold (folder : 'State -> _ -> 'State, state, charSet : CharSet) =
        Diet.fold folder state charSet.DietSet

    //
    static member FoldBack (folder : _ -> 'State -> 'State, state, charSet : CharSet) =
        Diet.foldBack folder charSet.DietSet state

    //
    static member Forall (predicate, charSet : CharSet) =
        Diet.forall predicate charSet.DietSet

    //
    static member IterateIntervals (action, charSet : CharSet) =
        let action = FSharpFunc<_,_,_>.Adapt action
        charSet.DietSet |> AvlTree.Iter action.Invoke

    interface System.IComparable with
        member this.CompareTo other =
            match other with
            | :? CharSet as other ->
                Diet.comparison this.DietSet other.DietSet
            | _ ->
                invalidArg "other" "The argument is not an instance of CharSet."

    interface System.IComparable<CharSet> with
        member this.CompareTo other =
            Diet.comparison dietSet other.DietSet

    interface System.IEquatable<CharSet> with
        member this.Equals other =
            Diet.equal dietSet other.DietSet


/// Functional programming operators related to the CharSet type.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CharSet =
    /// The empty set.
    let empty = CharSet.Empty

    /// Returns 'true' if the set is empty.
    let isEmpty charSet =
        CharSet.IsEmpty charSet

    /// The set containing the given element.
    let inline singleton c =
        CharSet.FromElement c

    /// The set containing the elements in the given range.
    let inline ofRange lowerBound upperBound =
        CharSet.FromRange (lowerBound, upperBound)

    /// Returns the number of elements in the set.
    let inline count (charset : CharSet) =
        charset.Count

    /// Returns the number of intervals in the set.
    let inline intervalCount (charset : CharSet) =
        charset.IntervalCount

    /// Returns a new set with an element added to the set.
    /// No exception is raised if the set already contains the given element.
    let inline add value charSet =
        CharSet.Add (value, charSet)

    //
    let inline addRange lower upper charSet =
        CharSet.AddRange (lower, upper, charSet)

    /// Returns a new set with the given element removed.
    /// No exception is raised if the set doesn't contain the given element.
    let inline remove value charSet =
        CharSet.Remove (value, charSet)

    /// Evaluates to 'true' if the given element is in the given set.
    let inline contains value charSet =
        CharSet.Contains (value, charSet)

    /// Applies the given accumulating function to all the elements of the set.
    let inline fold (folder : 'State -> char -> 'State) (state : 'State) charSet =
        CharSet.Fold (folder, state, charSet)

    /// Applies the given accumulating function to all the elements of the set.
    let inline foldBack (folder : char -> 'State -> 'State) charSet (state : 'State) =
        CharSet.FoldBack (folder, state, charSet)

//    /// Returns a new set containing the results of applying the given function to each element of the input set.
//    let map (mapping : char -> char) charSet =
//        (empty, charSet)
//        ||> fold (fun set el ->
//            add (mapping el) set)
//
//    /// Returns a new set containing only the elements of the collection for which the given predicate returns true.
//    let filter (predicate : char -> bool) charSet =
//        (empty, charSet)
//        ||> fold (fun set el ->
//            if predicate el then
//                add el set
//            else set)
//
//    /// Applies the given function to each element of the set, in order from least to greatest.
//    let iter (action : char -> unit) charSet =
//        iterImpl action charSet id

    /// Applies the given function to each element of the set, in order from least to greatest.
    let inline iterIntervals action charSet =
        CharSet.IterateIntervals (action, charSet)

//    /// Tests if any element of the collection satisfies the given predicate.
//    /// If the input function is <c>predicate</c> and the elements are <c>i0...iN</c>,
//    /// then this function computes predicate <c>i0 or ... or predicate iN</c>.
//    let exists (predicate : char -> bool) charSet =
//        existsImpl predicate charSet id

    /// Tests if all elements of the collection satisfy the given predicate.
    /// If the input function is <c>p</c> and the elements are <c>i0...iN</c>,
    /// then this function computes <c>p i0 && ... && p iN</c>.
    let inline forall (predicate : char -> bool) charSet =
        CharSet.Forall (predicate, charSet)

//    /// Creates a list that contains the elements of the set in order.
//    let inline toList charSet =
//        // Fold backwards so we don't need to reverse the list.
//        (tree, [])
//        ||> foldBack (fun i lst ->
//            i :: lst)

    /// Creates a set that contains the same elements as the given list.
    let inline ofList list =
        CharSet.OfList list

//    /// Creates a Set that contains the same elements as the given CharSet.
//    let toSet charSet =
//        (Set.empty, tree)
//        ||> fold (fun set el ->
//            Set.add el set)
//
//    /// Creates a CharSet that contains the same elements as the given Set.
//    let ofSet set =
//        (empty, set)
//        ||> Set.fold (fun tree el ->
//            add el tree)

//    /// Creates an array that contains the elements of the set in order.
//    let toArray charSet =
//        let resizeArr = ResizeArray<_> ()
//        iter resizeArr.Add tree
//        resizeArr.ToArray ()

    /// Creates a set that contains the same elements as the given array.
    let inline ofArray array =
        CharSet.OfArray array

//    /// Returns an ordered view of the set as an enumerable object.
//    let rec toSeq charSet =
//        seq {
//        match tree with
//        | Empty -> ()
//        | Node (lowerBound, upperBound, left, right) ->
//            // Produce the sequence for the left subtree.
//            yield! toSeq left
//
//            // Produce the sequence of values in this interval.
//            yield! { lowerBound .. upperBound }
//
//            // Produce the sequence for the right subtree.
//            yield! toSeq right }

    /// Creates a new set from the given enumerable object.
    let inline ofSeq seq =
        CharSet.OfSequence seq

    /// Returns the highest (greatest) value in the set.
    let inline maxElement (charSet : CharSet) =
        charSet.MaxElement

    /// Returns the lowest (least) value in the set.
    let inline minElement (charSet : CharSet) =
        charSet.MinElement

//    /// Splits the set into two sets containing the elements for which
//    /// the given predicate returns true and false respectively.
//    let partition predicate charSet =
//        ((empty, empty), set)
//        ||> fold (fun (trueSet, falseSet) el ->
//            if predicate el then
//                add el trueSet,
//                falseSet
//            else
//                trueSet,
//                add el falseSet)

    /// Returns a new set with the elements of the second set removed from the first.
    let inline difference set1 set2 =
        CharSet.Difference (set1, set2)

    /// Computes the intersection of the two sets.
    let inline intersect set1 set2 =
        CharSet.Intersect (set1, set2)

    /// Computes the union of the two sets.
    let inline union set1 set2 =
        CharSet.Union (set1, set2)


