﻿namespace FSharp.Data

open System
open System.Data
open Npgsql
open System.Data.Common
open System.Reflection

///<summary>Enum describing output type</summary>
type ResultType =
///<summary>Sequence of custom records with properties matching column names and types</summary>
    | Records = 0
///<summary>Sequence of tuples matching column types with the same order</summary>
    | Tuples = 1
///<summary>Typed DataTable <see cref='T:FSharp.Data.DataTable`1'/></summary>
    | DataTable = 2
///<summary>raw DataReader</summary>
    | DataReader = 3

module internal Const = 
    [<Literal>]
    let infraMessage = "This API supports the FSharp.Data.Npgsql infrastructure and is not intended to be used directly from your code."
    [<Literal>]
    let prohibitDesignTimeConnStrReUse = "Design-time connection string re-use allowed at run-time only when executed inside FSI."
    [<Literal>]
    let designTimeComponent = "FSharp.Data.Npgsql.DesignTime"

[<CompilerMessageAttribute(Const.infraMessage, 101, IsHidden = true)>]
type ISqlCommand = 
    abstract Execute: parameters: (string * obj)[] -> obj
    abstract AsyncExecute: parameters: (string * obj)[] -> obj

[<CompilerMessageAttribute(Const.infraMessage, 101, IsHidden = true)>]
[<RequireQualifiedAccess>]
type ResultRank = 
    | Sequence = 0
    | SingleRow = 1

[<CompilerMessageAttribute(Const.infraMessage, 101, IsHidden = true)>]
type DesignTimeConfig = {
    SqlStatement: string
    IsStoredProcedure: bool 
    Parameters: NpgsqlParameter[]
    ResultType: ResultType
    SingleRow: bool
    Row2ItemMapping: (obj[] -> obj)
    SeqItemTypeName: string
    ExpectedColumns: DataColumn[]
}

[<AutoOpen>]
[<CompilerMessageAttribute(Const.infraMessage, 101, IsHidden = true)>]
module Extensions =
    type internal DbDataReader with
        member this.MapRowValues<'TItem>( rowMapping) = 
            seq {
                use _ = this
                let values = Array.zeroCreate this.FieldCount
                while this.Read() do
                    this.GetValues(values) |> ignore
                    yield values |> rowMapping |> unbox<'TItem>
            }

    let DbNull = box DBNull.Value

[<CompilerMessageAttribute(Const.infraMessage, 101, IsHidden = true)>]
type ``ISqlCommand Implementation``(cfg: DesignTimeConfig, connection, commandTimeout) = 

    let cmd = new NpgsqlCommand(cfg.SqlStatement, CommandTimeout = commandTimeout)
    let readerBehavior = 
        CommandBehavior.SingleResult
        ||| if cfg.SingleRow then CommandBehavior.SingleRow else CommandBehavior.Default
        ||| if cfg.ResultType = ResultType.DataTable then CommandBehavior.KeyInfo else CommandBehavior.Default
        ||| match connection with Choice1Of2 _ -> CommandBehavior.CloseConnection | _ ->  CommandBehavior.Default

    let manageConnection = 
        match connection with
        | Choice1Of2 connectionString -> 
            cmd.Connection <- new NpgsqlConnection(connectionString)
            true

        | Choice2Of2 tran -> 
            cmd.Transaction <- tran 
            cmd.Connection <- tran.Connection
            false

    do
        cmd.CommandType <- if cfg.IsStoredProcedure then CommandType.StoredProcedure else CommandType.Text
        cmd.Parameters.AddRange( cfg.Parameters)
        
    let setupConnection() = 
        if cmd.Connection.State <> ConnectionState.Open && manageConnection
        then cmd.Connection.Open() 

    let asyncSetupConnection() = 
        async {
            if cmd.Connection.State <> ConnectionState.Open && manageConnection
            then 
                do! cmd.Connection.OpenAsync() |> Async.AwaitTask
        }

    static let seqToOption source =  
        match source |> Seq.truncate 2 |> Seq.toArray with
        | [||] -> None
        | [| x |] -> Some x
        | _ -> invalidArg "source" "The input sequence contains more than one element."

    let execute, asyncExecute = 
        match cfg.ResultType with
        | ResultType.DataReader -> 
            ``ISqlCommand Implementation``.ExecuteReader >> box, 
            ``ISqlCommand Implementation``.AsyncExecuteReader >> box
        | ResultType.DataTable ->
            ``ISqlCommand Implementation``.ExecuteDataTable >> box, 
            ``ISqlCommand Implementation``.AsyncExecuteDataTable >> box
        | ResultType.Records | ResultType.Tuples ->
            match box cfg.Row2ItemMapping, cfg.SeqItemTypeName with
            | null, null ->
                ``ISqlCommand Implementation``.ExecuteNonQuery manageConnection >> box, 
                ``ISqlCommand Implementation``.AsyncExecuteNonQuery manageConnection >> box
            | rowMapping, itemTypeName ->
                assert (rowMapping <> null && itemTypeName <> null)
                let itemType = Type.GetType( itemTypeName, throwOnError = true)
                
                let executeHandle = 
                    typeof<``ISqlCommand Implementation``>
                        .GetMethod("ExecuteSeq", BindingFlags.NonPublic ||| BindingFlags.Static)
                        .MakeGenericMethod(itemType)
                
                let asyncExecuteHandle = 
                    typeof<``ISqlCommand Implementation``>
                        .GetMethod("AsyncExecuteSeq", BindingFlags.NonPublic ||| BindingFlags.Static)
                        .MakeGenericMethod(itemType)
                        
                executeHandle.Invoke(null, [| cfg.Row2ItemMapping |]) |> unbox >> box, 
                asyncExecuteHandle.Invoke(null, [| cfg.Row2ItemMapping |]) |> unbox >> box

        | unexpected -> failwithf "Unexpected ResultType value: %O" unexpected

    member this.CommandTimeout = cmd.CommandTimeout

    interface ISqlCommand with

        member this.Execute parameters = execute(cmd, setupConnection, readerBehavior, parameters, cfg.ExpectedColumns)
        member this.AsyncExecute parameters = asyncExecute(cmd, asyncSetupConnection, readerBehavior, parameters, cfg.ExpectedColumns)

    interface IDisposable with
        member this.Dispose() =
            cmd.Dispose()

    static member internal SetParameters(cmd: NpgsqlCommand, parameters: (string * obj)[]) = 
        for name, value in parameters do
            
            let p = cmd.Parameters.[name]            

            if p.Direction.HasFlag(ParameterDirection.Input)
            then 
                if value = null 
                then 
                    p.Value <- DBNull.Value 
                else
                    p.Value <- value
            elif p.Direction.HasFlag(ParameterDirection.Output) && value :? Array
            then
                p.Size <- (value :?> Array).Length

    static member internal OptionToObj<'T> value = <@@ match %%value with Some (x : 'T) -> box x | None -> DbNull @@>    

    static member SetRef<'t>(r : byref<'t>, arr: (string * obj)[], i) = r <- arr.[i] |> snd |> unbox

    static member GetMapperWithNullsToOptions(nullsToOptions, mapper: obj[] -> obj) = 
        fun values -> 
            nullsToOptions values
            mapper values


//Execute/AsyncExecute versions

    static member internal VerifyOutputColumns(cursor: NpgsqlDataReader, expectedColumns: DataColumn[]) = 
        let verificationRequested = Array.length expectedColumns > 0
        if verificationRequested
        then 
            if  cursor.FieldCount < expectedColumns.Length
            then 
                let message = sprintf "Expected at least %i columns in result set but received only %i." expectedColumns.Length cursor.FieldCount
                cursor.Close()
                invalidOp message

            for i = 0 to expectedColumns.Length - 1 do
                let expectedName, expectedType = expectedColumns.[i].ColumnName, expectedColumns.[i].DataType
                let actualName, actualType = cursor.GetName( i), cursor.GetFieldType( i)
                let maybeEnum = 
                    (expectedType = typeof<string> && actualType = typeof<obj>)
                    || (expectedType = typeof<string[]> && actualType = typeof<Array>)
                let typeless = expectedType = typeof<obj> && actualType = typeof<string>
                if (expectedName <> "" && actualName <> expectedName) 
                    || (actualType <> expectedType && not maybeEnum && not typeless)
                then 
                    let message = sprintf """Expected column "%s" of type "%A" at position %i (0-based indexing) but received column "%s" of type "%A".""" expectedName expectedType i actualName actualType
                    cursor.Close()
                    invalidOp message

    static member internal ExecuteReader(cmd, setupConnection, readerBehavior, parameters, expectedColumns) = 
        ``ISqlCommand Implementation``.SetParameters(cmd, parameters)
        setupConnection() 
        let cursor = cmd.ExecuteReader(readerBehavior)
        ``ISqlCommand Implementation``.VerifyOutputColumns(cursor, expectedColumns)
        cursor

    static member internal AsyncExecuteReader(cmd, setupConnection, readerBehavior: CommandBehavior, parameters, expectedColumns) = 
        async {
            ``ISqlCommand Implementation``.SetParameters(cmd, parameters)
            do! setupConnection() 
            let! cursor = cmd.ExecuteReaderAsync( readerBehavior) |> Async.AwaitTask
            ``ISqlCommand Implementation``.VerifyOutputColumns(downcast cursor, expectedColumns)
            return cursor
        }
    
    static member internal ExecuteDataTable(cmd, setupConnection, readerBehavior, parameters, expectedColumns) = 
        use cursor = ``ISqlCommand Implementation``.ExecuteReader(cmd, setupConnection, readerBehavior, parameters, expectedColumns) 
        let result = new DataTable()
        result.Columns.AddRange(expectedColumns)
        result.Load(cursor)
        result

    static member internal AsyncExecuteDataTable(cmd, setupConnection, readerBehavior, parameters, expectedColumns) = 
        async {
            use! reader = ``ISqlCommand Implementation``.AsyncExecuteReader(cmd, setupConnection, readerBehavior, parameters, expectedColumns) 
            let result = new DataTable()
            result.Load(reader)
            return result
        }

    static member internal ExecuteSeq<'TItem> (rowMapper) = fun(cmd: NpgsqlCommand, setupConnection, readerBehavior, parameters, expectedColumns) -> 
        let hasOutputParameters = cmd.Parameters |> Seq.cast<NpgsqlParameter> |> Seq.exists (fun x -> x.Direction.HasFlag( ParameterDirection.Output))

        if not hasOutputParameters
        then 
            let xs = Seq.delay <| fun() -> 
                ``ISqlCommand Implementation``
                    .ExecuteReader(cmd, setupConnection, readerBehavior, parameters, expectedColumns)
                    .MapRowValues<'TItem>( rowMapper)

            if readerBehavior.HasFlag(CommandBehavior.SingleRow)
            then 
                xs |> seqToOption |> box
            else 
                box xs 
        else
            let resultset = 
                ``ISqlCommand Implementation``
                    .ExecuteReader(cmd, setupConnection, readerBehavior, parameters, expectedColumns)
                    .MapRowValues<'TItem>( rowMapper)
                    |> Seq.toList

            if hasOutputParameters
            then
                for i = 0 to parameters.Length - 1 do
                    let name, _ = parameters.[i]
                    let p = cmd.Parameters.[name]
                    if p.Direction.HasFlag( ParameterDirection.Output)
                    then 
                        parameters.[i] <- name, p.Value

            box resultset
            
    static member internal AsyncExecuteSeq<'TItem> (rowMapper) = fun(cmd, setupConnection, readerBehavior, parameters, expectedDataReaderColumns) ->
        let xs = 
            async {
                let! reader = ``ISqlCommand Implementation``.AsyncExecuteReader(cmd, setupConnection, readerBehavior, parameters, expectedDataReaderColumns)
                return reader.MapRowValues<'TItem>( rowMapper)
            }

        if readerBehavior.HasFlag(CommandBehavior.SingleRow)
        then
            async {
                let! xs = xs 
                return xs |> seqToOption
            }
            |> box
        else 
            box xs 

    static member internal ExecuteNonQuery manageConnection (cmd, _, _, parameters, _) = 
        ``ISqlCommand Implementation``.SetParameters(cmd, parameters)  
        try
            if manageConnection 
            then cmd.Connection.Open()

            let recordsAffected = cmd.ExecuteNonQuery() 
            for i = 0 to parameters.Length - 1 do
                let name, _ = parameters.[i]
                let p = cmd.Parameters.[name]
                if p.Direction.HasFlag( ParameterDirection.Output)
                then 
                    parameters.[i] <- name, p.Value
            recordsAffected
        finally
            if manageConnection 
            then cmd.Connection.Close()

    static member internal AsyncExecuteNonQuery manageConnection (cmd, _, _, parameters, _) = 
        ``ISqlCommand Implementation``.SetParameters(cmd, parameters)  
        async {         
            try 
                if manageConnection 
                then do! cmd.Connection.OpenAsync() |> Async.AwaitTask
                return! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            finally
                if manageConnection 
                then cmd.Connection.Close()
        }

    static member UpdateDataTable(table: DataTable, selectCommand, updateBatchSize, continueUpdateOnError, conflictOption) = 

        use dataAdapter = new NpgsqlDataAdapter(selectCommand, UpdateBatchSize = updateBatchSize, ContinueUpdateOnError = continueUpdateOnError)

        use commandBuilder = new NpgsqlCommandBuilder(dataAdapter)
        commandBuilder.ConflictOption <- conflictOption 

        use __ = dataAdapter.RowUpdating.Subscribe(fun args ->

            if  args.Errors = null 
                && args.StatementType = Data.StatementType.Insert 
                && dataAdapter.UpdateBatchSize = 1
            then 
                let columnsToRefresh = ResizeArray()
                for c in table.Columns do
                    if c.AutoIncrement  
                        || (c.AllowDBNull && args.Row.IsNull c.Ordinal)
                    then 
                        columnsToRefresh.Add( commandBuilder.QuoteIdentifier c.ColumnName)

                if columnsToRefresh.Count > 0
                then                        
                    let returningClause = columnsToRefresh |> String.concat "," |> sprintf " RETURNING %s"
                    let cmd = args.Command
                    cmd.CommandText <- cmd.CommandText + returningClause
                    cmd.UpdatedRowSource <- UpdateRowSource.FirstReturnedRecord
        )

        dataAdapter.Update(table)   

