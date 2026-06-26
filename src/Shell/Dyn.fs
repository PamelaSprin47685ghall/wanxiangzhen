module Wanxiangzhen.Shell.Dyn

open Fable.Core
open Fable.Core.JsInterop
open Fable.Core.JS

[<Global>]
let private structuredClone : obj -> obj = jsNative

let undefinedValue : obj = Unchecked.defaultof<obj>

let jsType (o: obj) : string = jsTypeof o

let isNullish (o: obj) : bool = isNull o || jsType o = "undefined"

let keys (o: obj) : string array =
    if isNullish o then [||]
    else JS.Constructors.Object.keys(o) |> Seq.toArray

let get (o: obj) (key: string) : obj =
    if isNullish o then undefinedValue else o?(key)

let getValue<'a> (o: obj) (key: string) : 'a =
    if isNullish o then unbox<'a> undefinedValue else o?(key)

let str (o: obj) (key: string) : string =
    let v = get o key
    if isNullish v then "" else string v

let opt (o: obj) (key: string) : obj option =
    let v = get o key
    if isNullish v then None else Some v

let has (o: obj) (key: string) : bool =
    not (isNullish o) && not (isNullish (o?(key)))

let typeIs (o: obj) (ty: string) : bool = jsType o = ty

let isArray (o: obj) : bool = JS.Constructors.Array.isArray(o)

let assignInto (target: obj) (source: obj) : obj =
    if not (isNullish source) then
        for key in keys source do
            target?(key) <- source?(key)
    target

let cloneShallow (o: obj) : obj =
    let copy = createObj []
    assignInto copy o |> ignore
    copy

let withKey (o: obj) (key: string) (v: obj) : obj =
    let copy = cloneShallow o
    copy?(key) <- v
    copy

let setKey (o: obj) (key: string) (v: obj) : unit = o?(key) <- v

let deleteKey (o: obj) (key: string) : unit =
    if not (isNullish o) then emitJsExpr (o, key) "delete $0[$1]" |> ignore

let call0 (f: obj) : obj = f $ ()
let call1 (f: obj) (a: obj) : obj = f $ a
let call2 (f: obj) (a: obj) (b: obj) : obj = f $ (a, b)

let clone (o: obj) : obj = structuredClone o

let truthy (o: obj) : bool =
    if isNullish o then false
    else
        match o with
        | :? bool as b -> b
        | :? int as i -> i <> 0
        | :? float as f -> f <> 0.0 && not (System.Double.IsNaN f)
        | :? string as s -> s.Length > 0
        | _ -> true
