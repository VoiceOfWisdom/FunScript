﻿module (*internal*) FunScript.InternalCompiler

open AST
open System
open System.Reflection
open Microsoft.FSharp.Quotations

type Helpers =
   static member Cast(x:obj) : 'a = failwith "never"

type IReturnStrategy =
   abstract Return: JSExpr -> JSStatement

type ICompiler = 
   abstract Compile: returnStategy:IReturnStrategy -> expr:Expr -> JSStatement list
   abstract ReplacementFor: MethodBase -> Quote.CallType -> MethodInfo option
   abstract NextTempVar: unit -> Var
   abstract DefineGlobal: Type -> string -> (Var -> JSStatement list) -> Var
   abstract DefineGlobalExplicit: string -> string -> (Var -> JSStatement list) -> Var
   abstract DefineGlobalInitialization: JSStatement list -> unit
   abstract Globals: JSStatement list

type ICompilerComponent =
   abstract TryCompile: compiler:ICompiler -> returnStategy:IReturnStrategy -> expr:Expr -> JSStatement list

type CallReplacer = 
  { Target: MethodBase
    TargetType: Quote.CallType
    Replacement: MethodInfo option
    TryReplace: ICompiler -> IReturnStrategy -> Expr option * Type [] * Expr list -> JSStatement list }
      
type JSModuleMember = 
  { Name: string;
    Var: Var;
    Statements: List<JSStatement> }
     
module JSModule =
   type T = 
     { Name:string; 
       Members: Map<string, JSModuleMember> }
   let create (name : string ) =
      {Name=name;Members=Map.empty}
   let addMember (jsModuleMember: JSModuleMember) (jsModule: T) =
      let members = jsModule.Members |> Map.add jsModuleMember.Name jsModuleMember
      { jsModule with Members = members }

type CompilerComponent =
   | CallReplacer of CallReplacer
   | CompilerComponent of ICompilerComponent

type Compiler(components, outputModules) as this = 
   let parameterKey (mb : MethodBase) =
      mb.GetParameters() |> Array.map (fun pi ->
         pi.ParameterType.Name)
   let key (mb:MethodBase) tt =
      mb.Name, mb.DeclaringType.Name, mb.DeclaringType.Namespace, tt
 
   let callerReplacers =
      components |> Seq.choose (function
         | CallReplacer r -> Some r
         | _ -> None) 
      |> Seq.groupBy (fun r -> key r.Target r.TargetType)
      |> Seq.map (fun (key, values) ->
         key, values |> Seq.map (fun r -> parameterKey r.Target, r) |> Map.ofSeq)
      |> Map.ofSeq

   let callerReplacements =
      callerReplacers |> Seq.choose (fun (KeyValue(id, rs)) ->
         let replacements =
            rs 
            |> Map.map (fun k r -> r.Replacement)
            |> Map.filter (fun k r -> r.IsSome)
            |> Map.map (fun k r -> r.Value)
         if replacements = Map.empty then
            None
         else Some (id, replacements))
      |> Map.ofSeq

   let rest = 
      components |> Seq.choose (function
         | CompilerComponent c -> Some c
         | _ -> None) |> Seq.toList

   let tryComponent returnStategy expr (part:ICompilerComponent) =
      match part.TryCompile this returnStategy expr with
      | [] -> None
      | procCodes -> Some procCodes

   let tryAllComponents returnStategy expr =
      let result = 
         rest |> List.tryPick(tryComponent returnStategy expr)
      match result with
      | None -> []
      | Some statements -> statements

   let getTypeArgs(mi:MethodBase) =
      if mi.IsConstructor then mi.DeclaringType.GetGenericArguments()
      else
         Array.append
            (mi.DeclaringType.GetGenericArguments())
            (mi.GetGenericArguments())

   let tryCompileCall callType returnStrategy mi obj exprs  =
      let this = this :> ICompiler
      match callerReplacers.TryFind (key mi callType) with
      | Some rs ->
         let paramKey = parameterKey mi
         let r =
            match rs.TryFind paramKey with
            | None -> 
                // Try to pick overload with same number of parameters.
                // Favour those with most like params.
                let rec ordering i (ks : _[]) acc =
                    if i = paramKey.Length && i = ks.Length then
                        acc
                    elif i = paramKey.Length || i = ks.Length then
                        Int32.MaxValue
                    elif paramKey.[i] = ks.[i] then
                        ordering (i+1) ks (acc-1)
                    else ordering (i+1) ks acc
                rs |> Map.toArray 
                |> Array.minBy (fun (ks, _) -> ordering 0 ks 0)
                |> snd
            | Some r -> r
         let typeArgs = getTypeArgs mi
         r.TryReplace this returnStrategy (obj, typeArgs, exprs)
      | None -> []
         
   let compile returnStategy expr =
      let replacementResult =
         match Quote.tryToMethodBase expr with
         | Some (obj, mi, exprs, callType) ->
            match Quote.specialOp mi with
            | Some opMi -> 
               match tryCompileCall callType returnStategy opMi obj exprs with
               | [] -> tryCompileCall callType returnStategy mi obj exprs
               | stmts -> stmts
            | None -> tryCompileCall callType returnStategy mi obj exprs
         | None -> []
      let result =
         match replacementResult with
         | [] -> tryAllComponents returnStategy expr
         | statements -> statements
      match result with
      | [] -> failwithf "Could not compile expression: %A" expr
      | statements -> statements

   let nextId = ref 0

   let mutable modules = Map.empty<string, JSModule.T>
   let updateModule (jsModule : JSModule.T) = 
      modules <- modules |> Map.add jsModule.Name jsModule
      
   let define (moduleName : string) (name : string) (cons : Var -> List<JSStatement>) =
      let jsModule = 
         match modules |> Map.tryFind moduleName with
            | Some (jsModule) -> jsModule 
            |None -> 
               let jsModule = JSModule.create(moduleName)
               updateModule jsModule
               jsModule
      //still have to put the full module name as the path to prevent collions when not compiling 
      //to modules. to fix this, check whether modules are enabled.
      let name = moduleName + "_" + name 

      match jsModule.Members |> Map.tryFind name with
      | Some (moduleMember) -> moduleMember.Var
      | None ->
             // Define upfront to avoid problems with mutually recursive methods
             let var = Var.Global(name, typeof<obj>)
             let moduleMember = {Name=name; Var=var; Statements=List.empty}
             let jsModule = jsModule |> JSModule.addMember moduleMember
             updateModule jsModule
             let assignment = cons var
             // more methods could have been added, need to get the latest module refernce
             let jsModule = 
                modules
                |> Map.find moduleName 
                |> JSModule.addMember {moduleMember with Var = var; Statements = assignment}

             updateModule jsModule
             var

   let mutable initialization = List.empty

   let getGlobals() =
      let globalMethods = 
         modules 
         |> Map.toList 
         |> List.map snd
         |> List.collect(fun m -> m.Members |> Map.toList |> List.map snd)

      let globals = 
         globalMethods
         |> List.map(fun x -> x.Var, x.Statements)

      let declarations = 
         match globals with
         | [] -> []
         | _ -> [Declare (globals |> List.map (fun (var, _) -> var))]

      let assignments = globals |> List.collect snd

      List.append
         (List.append declarations assignments)
         initialization

   let tryFixDeclaringTypeGenericParameters (originalMethod:MethodBase) (replacementMethod:MethodInfo) =
        let genericTypeDefinition = 
            if replacementMethod.DeclaringType.IsGenericType then Some(replacementMethod.DeclaringType.GetGenericTypeDefinition())
            elif replacementMethod.DeclaringType.IsGenericTypeDefinition then Some replacementMethod.DeclaringType
            else None
        match genericTypeDefinition with
        | None -> replacementMethod
        | Some gt ->
            let typedDeclaringType = gt.MakeGenericType(originalMethod.DeclaringType.GetGenericArguments())
            let flags = BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public
            typedDeclaringType.GetMethods(flags)
            |> Seq.append (
                typedDeclaringType.GetInterfaces() 
                |> Seq.collect (fun it -> typedDeclaringType.GetInterfaceMap(it).TargetMethods))
            |> Seq.find (fun m -> m.Name = replacementMethod.Name 
                                  && m.IsStatic = replacementMethod.IsStatic // TODO: We may need to make this comparison safer
                                  && m.GetParameters().Length = replacementMethod.GetParameters().Length) 

   member __.Compile returnStategy expr = 
      compile returnStategy expr

   member __.Globals = getGlobals()

   interface ICompiler with

      member __.Compile returnStategy expr = 
         compile returnStategy expr

      member __.ReplacementFor (pi:MethodBase) targetType =
         callerReplacements.TryFind (key pi targetType)
         |> Option.map (fun rs ->
            let paramKey = parameterKey pi
            let r =
               match rs.TryFind paramKey with
               | None -> (rs |> Seq.head).Value
               | Some r -> r
            tryFixDeclaringTypeGenericParameters pi r)

      member __.NextTempVar() = 
         incr nextId
         Var(sprintf "_%i" !nextId, typeof<obj>, false) 
         
      member __.DefineGlobal moduleType name cons =
         let moduleName = JavaScriptNameMapper.mapType moduleType
         define moduleName name cons
      
      member __.DefineGlobalExplicit moduleName name cons =
         define moduleName name cons
         
      member __.DefineGlobalInitialization stmts =
         initialization <- List.append initialization stmts

      member __.Globals = getGlobals()
         