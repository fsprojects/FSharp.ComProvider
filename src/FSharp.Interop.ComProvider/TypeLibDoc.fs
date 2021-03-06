﻿module private FSharp.Interop.ComProvider.TypeLibDoc

open System
open System.Runtime.InteropServices
open System.Runtime.InteropServices.ComTypes
open System.Reflection
open System.Collections.Generic
open Microsoft.FSharp.Core.CompilerServices
open ReflectionProxies
open Utility

let getStruct<'t when 't : struct> ptr freePtr =
    let str = Marshal.PtrToStructure(ptr, typeof<'t>) :?> 't
    freePtr ptr
    str

let getTypeLibDoc (typeLib:ITypeLib) =
    [ for typeIndex = 0 to typeLib.GetTypeInfoCount() - 1 do
        let typeInfo = typeLib.GetTypeInfo(typeIndex)
        let typeAttr = getStruct<TYPEATTR> (typeInfo.GetTypeAttr()) typeInfo.ReleaseTypeAttr
        let typeName, typeDoc, _, _ = typeInfo.GetDocumentation(-1)
        let memberDocs =
            [ for funcIndex = 0 to int typeAttr.cFuncs - 1 do
                let funcDesc = getStruct<FUNCDESC> (typeInfo.GetFuncDesc(funcIndex)) typeInfo.ReleaseFuncDesc
                let funcName, funcDoc, _, _ = typeInfo.GetDocumentation(funcDesc.memid)
                yield funcName, funcDoc ]
        yield typeName, (typeDoc, Map.ofSeq memberDocs) ]
    |> Map.ofSeq

let annotateAssembly typeDocs (asm:Assembly) =
    let toList (items:seq<'t>) = ResizeArray<'t> items :> IList<'t>

    let attrCons = typeof<TypeProviderXmlDocAttribute>.GetConstructor [| typeof<string> |]
    let attrData docString =
        { new CustomAttributeData() with
            override __.Constructor = attrCons
            override __.ConstructorArguments = [ CustomAttributeTypedArgument docString ] |> toList }
    let addAttr docString (memb:MemberInfo) =
        if String.IsNullOrWhiteSpace docString then []
        else [attrData docString]
        |> Seq.append (memb.GetCustomAttributesData())
        |> toList

    let findSourceInterface (ty:Type) =
        match ty.TryGetAttribute<ComEventInterfaceAttribute>() with
        | Some attr -> attr.SourceInterface
        | _ -> ty

    let findRelatedMember (memb:MemberInfo) =
        memb.DeclaringType.GetEvents()
        |> Seq.tryFind (fun event ->
            [ event.GetAddMethod(); event.GetRemoveMethod() ]
            |> Seq.exists(fun m -> m :> MemberInfo = memb))
        |> function
            | Some event -> event :> MemberInfo
            | None -> memb

    let typeDoc (ty:Type) =
        typeDocs
        |> Map.tryFind ty.Name
        |> Option.map fst

    let memberDoc (memb:MemberInfo) =
        let ty = memb.DeclaringType
        ty.GetInterfaces()
        |> Seq.append [ty]
        |> Seq.map findSourceInterface
        |> Seq.choose (fun ty -> typeDocs |> Map.tryFind ty.Name)
        |> Seq.choose (fun (_, membs) -> membs |> Map.tryFind (findRelatedMember memb).Name)
        |> Seq.tryFind (not << String.IsNullOrEmpty)

    let annotate getDoc addAnnotation (memb:#MemberInfo) =
        let doc = defaultArg (getDoc memb) ""
        addAnnotation (addAttr doc memb) memb

    let annotateEvent = annotate memberDoc <| fun data event ->
         { new EventInfoProxy(event) with
            override __.GetCustomAttributesData() = data } :> EventInfo

    let annotateMethod = annotate memberDoc <| fun data meth ->
        { new MethodInfoProxy(meth) with
            override __.GetCustomAttributesData() = data } :> MethodInfo

    let annotateProperty = annotate memberDoc <| fun data prop ->
        { new PropertyInfoProxy(prop) with
            override __.GetCustomAttributesData() = data } :> PropertyInfo

    let annotateType = annotate typeDoc <| fun attr ty ->
        { new TypeProxy(ty) with
            override __.GetCustomAttributesData() = attr
            override __.GetEvents(flags) = ty.GetEvents(flags) |> Array.map annotateEvent
            override __.GetMethods(flags) = ty.GetMethods(flags) |> Array.map annotateMethod
            override __.GetProperties(flags) = ty.GetProperties(flags) |> Array.map annotateProperty } :> Type

    { new AssemblyProxy(asm) with
        override __.GetTypes() = asm.GetTypes() |> Array.map annotateType } :> Assembly
