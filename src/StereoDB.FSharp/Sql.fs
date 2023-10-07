﻿namespace StereoDB.Sql

open FParsec
open System.Linq.Expressions
open StereoDB.FSharp

module internal SqlParser =

    let ws = spaces
    let str_ws s = pstring s .>> ws
    let strCI_ws s = pstringCI s .>> ws
    let float_ws = pfloat .>> ws
    let int_ws = pint64 .>> ws

    let identifier =
        let isIdentifierFirstChar c = isLetter c || c = '_'
        let isIdentifierChar c = isLetter c || isDigit c || c = '_'

        many1Satisfy2L isIdentifierFirstChar isIdentifierChar "identifier" .>> ws // skips trailing whitespace

    let identifier_ws = identifier .>> ws

    type SqlPrimitiveExpression = 
        | SqlIntConstant of int64
        | SqlFloatConstant of float
        | SqlIdentifier of string
    type SqlExpression = 
        | BinaryArithmeticOperator of SqlExpression * string * SqlExpression
        | UnaryArithmeticOperator of string * SqlExpression
        | Primitive of SqlPrimitiveExpression

    type SqlLogicalExpression =
        | BinaryLogicalOperator of SqlLogicalExpression * string * SqlLogicalExpression
        | BinaryComparisonOperator of SqlExpression * string * SqlExpression
        | UnaryLogicalOperator of string * SqlLogicalExpression
        | IsNull of SqlExpression
        | IsNotNull of SqlExpression

    type Resultset = 
        | TableResultset of string

    type SelectListItem =
        | AliasedExpression of SqlExpression * string option
    type SelectClause = SelectListItem list
    type SetListItem = string * SqlExpression
    type SetClause = SetListItem list
    type FromClause = 
        | Resultset of Resultset
    type WhereClause = 
        | WhereCondition of SqlLogicalExpression

    type Query =
    | SelectQuery of (SelectClause * (FromClause * WhereClause option) option)
    | UpdateQuery of (string * SetClause * WhereClause option)

    let SQL_INT_CONSTANT = int_ws |>> SqlIntConstant
    let SQL_FLOAT_CONSTANT = float_ws |>> SqlFloatConstant
    let SQL_IDENTIFIER = identifier |>> SqlIdentifier
    let SQL_EXPRESSION = SQL_INT_CONSTANT <|> SQL_FLOAT_CONSTANT <|> SQL_IDENTIFIER |>> Primitive

    let arithOpp = new OperatorPrecedenceParser<SqlExpression,unit,unit>()
    let arithExpr = arithOpp.ExpressionParser
    let arithExpressionTerm = (SQL_EXPRESSION) <|> between (str_ws "(") (str_ws ")") arithExpr
    arithOpp.TermParser <- arithExpressionTerm

    arithOpp.AddOperator(InfixOperator("+", ws, 1, Associativity.Left, fun x y -> BinaryArithmeticOperator(x, "+", y)))
    arithOpp.AddOperator(InfixOperator("-", ws, 1, Associativity.Left, fun x y -> BinaryArithmeticOperator(x, "-", y)))
    arithOpp.AddOperator(InfixOperator("*", ws, 2, Associativity.Left, fun x y -> BinaryArithmeticOperator(x, "*", y)))
    arithOpp.AddOperator(InfixOperator("/", ws, 2, Associativity.Left, fun x y -> BinaryArithmeticOperator(x, "/", y)))
    arithOpp.AddOperator(PrefixOperator("-", ws, 3, false, fun x -> UnaryArithmeticOperator("-", x)))

    let flatten v =
        match v with
        | ((a, b), c) -> (a, b, c) 
     //   | _ -> failwith "Invalid tuple to flatten"

    let logicOpp = new OperatorPrecedenceParser<SqlLogicalExpression,unit,unit>()
    let SQL_LOGICAL_EXPRESSION = logicOpp.ExpressionParser
    let primitiveLogicalExpression =
        (SQL_EXPRESSION .>>? strCI_ws "IS" .>>? strCI_ws "NULL" |>> IsNull)
        <|> (SQL_EXPRESSION .>>? strCI_ws "IS" .>>? strCI_ws "NOT" .>> strCI_ws "NULL" |>> IsNotNull)
        <|> (SQL_EXPRESSION .>>.? strCI_ws "=" .>>. SQL_EXPRESSION |>> flatten |>> BinaryComparisonOperator)
        <|> (SQL_EXPRESSION .>>.? strCI_ws "<>" .>>. SQL_EXPRESSION |>> flatten |>> BinaryComparisonOperator)
        <|> (SQL_EXPRESSION .>>.? strCI_ws ">=" .>>. SQL_EXPRESSION |>> flatten |>> BinaryComparisonOperator)
        <|> (SQL_EXPRESSION .>>.? strCI_ws "<=" .>>. SQL_EXPRESSION |>> flatten |>> BinaryComparisonOperator)
    let logicExpressionTerm = (primitiveLogicalExpression) <|> between (str_ws "(") (str_ws ")") SQL_LOGICAL_EXPRESSION
    logicOpp.TermParser <- logicExpressionTerm

    logicOpp.AddOperator(InfixOperator("AND", ws, 1, Associativity.Left, fun x y -> BinaryLogicalOperator(x, "+", y)))
    logicOpp.AddOperator(InfixOperator("OR", ws, 1, Associativity.Left, fun x y -> BinaryLogicalOperator(x, "-", y)))
    logicOpp.AddOperator(PrefixOperator("NOT", ws, 3, false, fun x -> UnaryLogicalOperator("NOT", x)))

    let ALIASED_EXPRESSION = SQL_EXPRESSION .>>. opt (strCI_ws "AS" >>. identifier) |>> AliasedExpression
    let SELECT_LIST = sepBy ALIASED_EXPRESSION (str_ws ",")
    let ASSIGNMENT_EXPRESSION = identifier_ws .>> str_ws "=" .>>. arithExpr |>> SetListItem
    let SET_LIST = strCI_ws "SET" >>. sepBy ASSIGNMENT_EXPRESSION (str_ws ",") //|>> SetClause
    let TABLE_RESULTSET = identifier |>> TableResultset
    let FROM_CLAUSE = strCI_ws "FROM" >>. TABLE_RESULTSET |>> Resultset
    let WHERE_CLAUSE = strCI_ws "WHERE" >>. SQL_LOGICAL_EXPRESSION |>> WhereCondition
    let SELECT_STATEMENT = 
        spaces .>> strCI_ws "SELECT" >>. SELECT_LIST .>>. 
            (opt (FROM_CLAUSE .>>. (opt WHERE_CLAUSE))) |>> SelectQuery

    let UPDATE_STATEMENT =
        spaces .>> strCI_ws "UPDATE" >>. identifier_ws .>>. SET_LIST .>>. 
        (opt (WHERE_CLAUSE)) |>> flatten |>> UpdateQuery

    let QUERY = SELECT_STATEMENT <|> UPDATE_STATEMENT

    let parseSql str =
        match run QUERY str with
        | Success(result, _, _)   ->
            result
        | Failure(errorMsg, _, _) -> failwithf "Failure: %s" errorMsg

module Expr = 
    open System.Reflection
    open FSharp.Quotations.DerivedPatterns
    open FSharp.Quotations.Patterns
    let onlyVar = function Var v -> Some v | _ -> None

    let (|Property|_|) = function
        | PropertyGet(_, info, _) -> Some info
        | Lambda(arg, PropertyGet(Some(Var var), info, _))
        | Let(arg, _, PropertyGet(Some(Var var), info, _))
            when arg = var -> Some info
        | _ -> None

    let private (|LetInCall|_|) expr =
        let rec loop e collectedArgs =
            match e with
            | Let(arg, _, exp2) ->  
                let newArgs = Set.add arg collectedArgs
                loop exp2 newArgs
            | Call(_instance, mi, args) ->
                let setOfCallArgs =
                    args
                    |> List.choose onlyVar
                    |> Set.ofList
                if Set.isSubset setOfCallArgs collectedArgs then
                    Some mi
                else None
            | _ -> None
        loop expr Set.empty

    let private (|Func|_|) = function
        // function values without arguments
        | Lambda (arg, Call (target, info, []))
            when arg.Type = typeof<unit> -> Some (target, info)
        // function values with one argument
        | Lambda (arg, Call (target, info, [Var var]))
            when arg = var -> Some (target, info)
        // function values with a set of curried or tuple arguments
        | Lambdas (args, Call (target, info, exprs)) ->
            let justArgs = List.choose onlyVar exprs
            let allArgs = List.concat args
            if justArgs = allArgs then
                Some (target, info)
            else None
        | Lambdas(_args, _body) ->
            None
        | _ -> None

    let (|Method|_|) = function
        // any ordinary calls: foo.Bar ()
        | Call (_, info, _) -> Some info
        // calls and function values via a lambda argument:
        // fun (x: string) -> x.Substring (1, 2)
        // fun (x: string) -> x.StartsWith
        | Lambda (arg, Call (Some (Var var), info, _))
        | Lambda (arg, Func (Some (Var var), info))
            when arg = var -> Some info
        // any function values:someString.StartsWith
        | Func (_, info) -> Some info
        // calls and function values ​​via instances:
        // "abc" .StartsWith ("a")
        // "abc" .Substring
        | Let (arg, _, Call (Some (Var var), info, _))
        | Let (arg, _, Func (Some (Var var), info))
            when arg = var -> Some info
        | LetInCall(info) -> Some info
        | _ -> None
        
    /// Get a MethodInfo from an expression that is a method call or a function type value
    let methodof = function Method mi -> mi | _ -> failwith "Not a method expression"

module internal QueryBuilder =
    open SqlParser

    type QueryExecution<'TSchema> =
        | Write of System.Action<ReadWriteTsContext<'TSchema>>
        | Read of System.Func<ReadOnlyTsContext<'TSchema>, obj>

    type SchemaMetadata(schema) =
        let schemaType = schema.GetType()
        member this.TryGetTable tableName = 
            let schemaProperty = schemaType.GetProperty(tableName)
            if schemaProperty <> null then
                let tableEntityType = schemaProperty.PropertyType.GenericTypeArguments[0].GenericTypeArguments[1]
                let keyType = tableEntityType.GetInterfaces() |> Seq.find (fun ifType -> ifType.Name = "IEntity`1")
                Some (schemaProperty, tableEntityType, keyType.GenericTypeArguments[0])
            else None
        member this.TryGetTableColumn tableName columnName = 
            this.TryGetTable tableName
                |> Option.bind (fun (tableProperty, entityType, keyType) -> 
                        let columnProperty = entityType.GetProperty(columnName)
                        if columnProperty <> null then
                            Some columnProperty
                        else None)

    let iter<'T> (action: System.Action<'T>) (seq: 'T voption seq) =
        Seq.iter (fun x ->
            match x with 
            | ValueSome x -> action.Invoke(x)
            | _ -> ()) seq

    let map (action: System.Func<'T, 'TResult>) (seq: 'T voption seq) =
        Seq.map (fun x ->
            match x with 
            | ValueSome x -> action.Invoke(x)
            | _ -> failwith "Cannot apply SELECT on non-row") seq

    let buildTableScan tableType tableEntityType keyType =
        let rot = typeof<IReadOnlyTable<_, _>>.GetGenericTypeDefinition().MakeGenericType(keyType, tableEntityType)

        // Build Table scan
        // let tableScan entity = 
        //     let ids = entity.GetIds()
        //     ids
        //         |> System.Linq.Enumerable.Select (fun id -> entity.Get id)
        let entity = Expression.Parameter(tableType, "entity")
        let getIdsCall = Expression.Call(entity, rot.GetMethod("GetIds"))
            
        let x = Expression.Parameter(keyType, "x")
        let enumerableType = typeof<System.Linq.Enumerable>
            
        let getMethod = rot.GetMethod("Get")
        let getIdLambda = Expression.Lambda (Expression.Call(entity, getMethod, x), x)
        let selectMethod = enumerableType.GetMethods() |> Seq.filter (fun c -> c.Name = "Select") |> Seq.head
        let result = Expression.Call(selectMethod.MakeGenericMethod(keyType, getMethod.ReturnType), getIdsCall, getIdLambda)
        let tableScanExpresion = Expression.Lambda (result, entity)
        tableScanExpresion

    let buildQuery<'TSchema, 'TResult> (query: Query) executionContext schema =
        let metadata = SchemaMetadata(schema)
        let schemaType = typeof<'TSchema>
        match query with
        | SelectQuery (sel, body) ->
            let resultsetType = typeof<System.Collections.Generic.List<'TResult>>
            // let resutlt = List<'TResult>()
            // 
            // let selectProjection = fun row -> (row.Title)
            // let whereFilter = fun row -> row.Quantity > 100
            // let tableScan entity = 
            //     let ids = entity.GetIds()
            //     ids
            //         |> Seq.map (fun id -> entity.Get id)
               
            // let actualTable = ctx.UseTable(ctx.Schema.Books.Table)
            // tableScan actualTable
            //     |> Seq.filter whereFilter
            //     |> Seq.map selectProjection
            let contextType = typeof<ReadOnlyTsContext<'TSchema>>
            let context = Expression.Parameter(contextType, "context")
            let schemaAccess = Expression.Property(context, "Schema")

            match body with
            | Some (fromClause, whereClause) ->
                let tableName =
                    match fromClause with
                    | Resultset(TableResultset(tableName)) -> tableName
                let (table, tableEntityType, keyType) = 
                    match metadata.TryGetTable tableName with
                    | Some t -> t
                    | None -> failwith $"Table {tableName} is not defined"
                let findColumn column = 
                    let column = 
                        match metadata.TryGetTableColumn tableName column with
                        | Some c -> c
                        | None -> failwith $"Column {column} does not exist in table {tableName}"
                    column

                let buildSelectProjection () =
                    // Build Update projection
                    // let updateProjection = fun row -> 
                    //   row.Quantity = 100
                    //   ()
                    let row = Expression.Parameter(tableEntityType, "row");
                    let buildExpression expression :Expression =
                        match expression with
                        | BinaryArithmeticOperator (left, op, right) -> failwith "Not implemented"
                        | UnaryArithmeticOperator (op, expr) -> failwith "Not implemented"
                        | Primitive primitive ->
                            match primitive with
                            | SqlFloatConstant c -> Expression.Constant(c, typeof<float>)
                            | SqlIntConstant c -> if keyType = typeof<int> then Expression.Constant(c |> int, typeof<int>) else Expression.Constant(c, typeof<int64>)
                            | SqlIdentifier identifier -> Expression.Property(row, findColumn identifier)

                    let namedSelect = 
                        sel |> List.map (fun x ->
                            match x with
                            | AliasedExpression (expr, Some(alias)) -> (alias, expr)
                            | AliasedExpression (expr, None) ->
                                match expr with
                                | Primitive prim -> match prim with
                                                    | SqlIdentifier ident -> (ident, expr)
                                                    | _ -> failwith "Column name cannot be determined"
                                | _ -> failwith "Column name cannot be determined")

                    let selectProjectionType = typeof<'TResult>
                    let updateBlock: Expression = 
                        let constructorParameters =
                            selectProjectionType.GetProperties() |> Array.map (fun prop -> 
                                let shouldBeUpdated = namedSelect |> Seq.tryFind (fun (alias, expr) -> alias = prop.Name)
                                match shouldBeUpdated with
                                | Some (column, expression) -> 
                                    let newValueExpression = buildExpression expression
                                    newValueExpression
                                | None -> Expression.Property(row, prop)
                                )
                        let constructor = selectProjectionType.GetConstructors() |> Seq.head
                        let createNew = Expression.New(constructor, constructorParameters)
                        createNew

                    let updateLambdaType = (typeof<System.Func<_, _>>).GetGenericTypeDefinition().MakeGenericType(tableEntityType, selectProjectionType)
                    let updateProjectionExpresion = Expression.Lambda (updateLambdaType, updateBlock, row)
                    updateProjectionExpresion

                let tablePropertyAccess = Expression.Property(schemaAccess, schemaType.GetProperty(tableName))
                let tet = schemaType.GetProperty(tableName).PropertyType.GetProperty("Table")
                let tableExpression: Expression = Expression.Property(tablePropertyAccess, tet)
                let useTableMethod = contextType.GetMethod("UseTable").MakeGenericMethod(keyType, tableEntityType)
                let tableAccessor = Expression.Call(context, useTableMethod, tableExpression)
                let rot = typeof<IReadOnlyTable<_, _>>.GetGenericTypeDefinition().MakeGenericType(keyType, tableEntityType)
                let actualTable = Expression.Variable(rot, "actualTable");
                let actualTableAssignment = Expression.Assign(actualTable, tableAccessor)

                let tableScanExpresion = buildTableScan rot tableEntityType keyType
                let selectProjection = buildSelectProjection ()

                // Create table scan
                let tableScanResultType = typeof<seq<_>>.GetGenericTypeDefinition().MakeGenericType(typeof<obj voption>.GetGenericTypeDefinition().MakeGenericType(tableEntityType))
                let tableScan = Expression.Variable(typeof<System.Func<_, _>>.GetGenericTypeDefinition().MakeGenericType(rot, tableScanResultType), "tableScan");
                let tableScanAssignment = Expression.Assign(tableScan, tableScanExpresion)
                let createTableScanExpression = Expression.Invoke(tableScan, actualTable)

                let createNew = Expression.New(resultsetType)
                let result = Expression.Variable(resultsetType, "result");
                let resultAssignment = Expression.Assign(result, createNew)

                // Apply select to each object
                let seqIter = Expr.methodof <@ map @>
                let resultExpression = Expression.Call(seqIter.GetGenericMethodDefinition().MakeGenericMethod(tableEntityType, typeof<'TResult>), selectProjection, createTableScanExpression)
                let addRangeCall = Expression.Call(result, resultsetType.GetMethod("AddRange"), resultExpression);
                let letFunction = Expression.Block([| result; actualTable; tableScan |], actualTableAssignment, tableScanAssignment, resultAssignment, addRangeCall, result)
                //let letFunction = Expression.Block([| result |], resultAssignment)
                let resultLambdaType = typeof<System.Func<ReadOnlyTsContext<'TSchema>, obj>>
                let readTransaction = Expression.Lambda (resultLambdaType, letFunction, context)
                Read (readTransaction.Compile() :?> System.Func<ReadOnlyTsContext<'TSchema>, obj>)
            | None -> failwith "SELECT without FROM not yet supported"
        | UpdateQuery (tableName, set, filter) ->
            let (table, tableEntityType, keyType) = 
                match metadata.TryGetTable tableName with
                | Some t -> t
                | None -> failwith $"Table {tableName} is not defined"
            let findColumn column = 
                let column = 
                    match metadata.TryGetTableColumn tableName column with
                    | Some c -> c
                    | None -> failwith $"Column {column} does not exist in table {tableName}"
                column
            let contextType = typeof<ReadWriteTsContext<'TSchema>>
            let rwt = typeof<IReadWriteTable<_, _>>.GetGenericTypeDefinition().MakeGenericType(keyType, tableEntityType)

            let buildUpdateProjection actualTable =
                // Build Update projection
                // let updateProjection = fun row -> 
                //   row.Quantity = 100
                //   ()
                let row = Expression.Parameter(tableEntityType, "row");
                let buildExpression expression :Expression =
                    match expression with
                    | BinaryArithmeticOperator (left, op, right) -> failwith "Not implemented"
                    | UnaryArithmeticOperator (op, expr) -> failwith "Not implemented"
                    | Primitive primitive ->
                        match primitive with
                        | SqlFloatConstant c -> Expression.Constant(c, typeof<float>)
                        | SqlIntConstant c -> if keyType = typeof<int> then Expression.Constant(c |> int, typeof<int>) else Expression.Constant(c, typeof<int64>)
                        | SqlIdentifier identifier -> Expression.Property(row, findColumn identifier)

                let allPropertiesWriteable = set |> List.forall (fun (column,_) -> (findColumn column).CanWrite)
                let updateBlock: Expression = 
                    if allPropertiesWriteable then
                        // directly update property
                        let setExpressions = set |> List.map (fun (column, expression) -> 
                            let newValueExpression = buildExpression expression
                            let targetProperty = findColumn column
                            let columnExpression = Expression.Property(row, targetProperty)
                            let assignment = Expression.Assign(columnExpression, newValueExpression)
                            assignment :> Expression)
                        Expression.Block(setExpressions)
                    else
                        let setMethod = rwt.GetMethod("Set")
                        let constructorParameters =
                            tableEntityType.GetProperties() |> Array.map (fun prop -> 
                                let shouldBeUpdated = set |> Seq.tryFind (fun (c, e) -> c = prop.Name)
                                match shouldBeUpdated with
                                | Some (column, expression) -> 
                                    let newValueExpression = buildExpression expression
                                    newValueExpression
                                | None -> Expression.Property(row, prop)
                                )
                        let constructor = tableEntityType.GetConstructors() |> Seq.head
                        let createNew = Expression.New(constructor, constructorParameters)
                        Expression.Call(actualTable, setMethod, createNew)

                let updateLambdaType = (typeof<System.Action<_>>).GetGenericTypeDefinition().MakeGenericType(tableEntityType)
                let updateProjectionExpresion = Expression.Lambda (updateLambdaType, updateBlock, row)
                updateProjectionExpresion

            // Composing things
            //
            // let actualTable = ctx.UseTable(ctx.Schema.Books.Table)
            // tableScan actualTable
            //     |> Seq.filter whereFilter
            //     |> Seq.iter updateProjection
            let context = Expression.Parameter(contextType, "context")
            let schemaAccess = Expression.Property(context, "Schema")
            let tablePropertyAccess = Expression.Property(schemaAccess, schemaType.GetProperty(tableName))
            let tet = schemaType.GetProperty(tableName).PropertyType.GetProperty("Table")
            let tableExpression: Expression = Expression.Property(tablePropertyAccess, tet)
            let useTableMethod = contextType.GetMethod("UseTable").MakeGenericMethod(keyType, tableEntityType)
            let tableAccessor = Expression.Call(context, useTableMethod, tableExpression)
            let actualTable = Expression.Variable(rwt, "actualTable");
            let actualTableAssignment = Expression.Assign(actualTable, tableAccessor)

            let updateProjectionExpresion = buildUpdateProjection actualTable
            let tableScanExpresion = buildTableScan rwt tableEntityType keyType

            // Create table scan
            let tableScanResultType = typeof<seq<_>>.GetGenericTypeDefinition().MakeGenericType(typeof<obj voption>.GetGenericTypeDefinition().MakeGenericType(tableEntityType))
            let tableScan = Expression.Variable(typeof<System.Func<_, _>>.GetGenericTypeDefinition().MakeGenericType(rwt, tableScanResultType), "tableScan");
            let tableScanAssignment = Expression.Assign(tableScan, tableScanExpresion)
            let createTableScanExpression = Expression.Invoke(tableScan, actualTable)

            // Apply update to each object
            let seqIter = Expr.methodof <@ iter @>
            let resultExpression = Expression.Call(seqIter.GetGenericMethodDefinition().MakeGenericMethod(tableEntityType), updateProjectionExpresion, createTableScanExpression)
            let letFunction = Expression.Block([| actualTable; tableScan |], actualTableAssignment, tableScanAssignment, resultExpression)
            let resultLambdaType = typeof<System.Action<ReadWriteTsContext<'TSchema>>>
            let writeTransaction = Expression.Lambda (resultLambdaType, letFunction, context)
            
            Write (writeTransaction.Compile() :?> System.Action<ReadWriteTsContext<'TSchema>>)
