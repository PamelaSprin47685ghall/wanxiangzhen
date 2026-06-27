module Wanxiangzhen.Tests.DynTests

open Fable.Core
open Fable.Core.JsInterop
open Fable.Core.JS
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Tests.Assert

[<Emit("[1, 2, 3]")>]
let private jsArray : obj = jsNative

let private mkObj (pairs: (string * obj) list) : obj =
    createObj (pairs |> List.map (fun (k, v) -> k ==> v))

let private mkNull : obj = null :> obj
let private mkUndef : obj = Unchecked.defaultof<obj>

let entries () : (string * (unit -> unit)) list = [

    ("Dyn.isNullish null", fun () ->
        check (isNullish mkNull))

    ("Dyn.isNullish undefined", fun () ->
        check (isNullish mkUndef))

    ("Dyn.isNullish emptyString", fun () ->
        check (not (isNullish "")))

    ("Dyn.isNullish obj", fun () ->
        let o = mkObj ["x" ==> box 1]
        check (not (isNullish o)))

    ("Dyn.keys returns key array", fun () ->
        let o = mkObj ["a" ==> box 1; "b" ==> box 2]
        let k = keys o
        check (k.Length = 2)
        check (Seq.exists (fun key -> key = "a") k)
        check (Seq.exists (fun key -> key = "b") k))

    ("Dyn.get existing key", fun () ->
        let o = mkObj ["x" ==> box 42]
        equal 42 (unbox<int> (get o "x")))

    ("Dyn.get missing key returns undefinedValue", fun () ->
        let o = mkObj ["x" ==> box 1]
        check (isNullish (get o "no-such-key")))

    ("Dyn.str existing returns string", fun () ->
        let o = mkObj ["s" ==> "hello"]
        equal "hello" (str o "s"))

    ("Dyn.str missing returns empty string", fun () ->
        let o = mkObj ["x" ==> box 1]
        equal "" (str o "no-such-key"))

    ("Dyn.opt existing -> Some", fun () ->
        let o = mkObj ["v" ==> box 99]
        isSome (opt o "v"))

    ("Dyn.opt missing -> None", fun () ->
        let o = mkObj ["x" ==> box 1]
        isNone (opt o "no-such-key"))

    ("Dyn.has existing -> true", fun () ->
        let o = mkObj ["k" ==> box 1]
        check (has o "k"))

    ("Dyn.has missing -> false", fun () ->
        let o = mkObj ["k" ==> box 1]
        check (not (has o "no-such-key")))

    ("Dyn.typeIs object", fun () ->
        let o = mkObj []
        check (typeIs o "object"))

    ("Dyn.isArray true for array", fun () ->
        check (isArray jsArray))

    ("Dyn.isArray false for non-array", fun () ->
        let o = mkObj ["x" ==> box 1]
        check (not (isArray o)))

    ("Dyn.truthy null false", fun () ->
        check (not (truthy mkNull)))

    ("Dyn.truthy 0 false", fun () ->
        check (not (truthy (box 0))))

    ("Dyn.truthy emptyString false", fun () ->
        check (not (truthy "")))

    ("Dyn.truthy true", fun () ->
        check (truthy (box true)))

    ("Dyn.truthy emptyObject true", fun () ->
        check (truthy (mkObj [])))

    ("Dyn.withKey adds key to new object", fun () ->
        let o = mkObj ["a" ==> box 1]
        let c = withKey o "b" (box 2)
        check (has c "b")
        equal 2 (unbox<int> (get c "b"))
        // original unchanged
        check (not (has o "b")))

    ("Dyn.cloneShallow independent copy", fun () ->
        let o = mkObj ["x" ==> box 1]
        let c = cloneShallow o
        equal 1 (unbox<int> (get c "x"))
        // setting copy must not affect original
        setKey c "x" (box 99)
        equal 1 (unbox<int> (get o "x")))

    ("Dyn.setKey mutates object", fun () ->
        let o = mkObj ["k" ==> box 1]
        setKey o "k" (box 42)
        equal 42 (unbox<int> (get o "k")))

    ("Dyn.deleteKey removes key", fun () ->
        let o = mkObj ["k" ==> box 1]
        deleteKey o "k"
        check (not (has o "k")))
]
