open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open DSharpPlus
open DSharpPlus.CommandsNext
open DSharpPlus.CommandsNext.Attributes
open DSharpPlus.Entities

// -----------------------------
// Configuration & constants
// -----------------------------

// Currency: we store everything in cents (integers) to avoid floating rounding issues.
// $22.00 per hour = 2200 cents per hour.
let centsPerHour = 2200.0M  // decimal for precision
// We'll credit every minute, so compute cents per minute.
let centsPerMinute = centsPerHour / 60.0M  // ~36.666... cents per minute

// File where balances and enrollments are stored
let storageFile = "balances.json"

// Background tick interval (in milliseconds). We'll tick every 60s (60000 ms).
let tickMs = 60 * 1000

// -----------------------------
// Data types & serialization helpers
// -----------------------------

// Represent each user's stored data
type UserData =
    { BalanceCents: int64            // whole cents (integer)
      RemainderCents: decimal       // fractional cents accumulator (< 1.0 normally) }

// JSON-friendly mapping type
type Storage =
    { Users: Map<string, UserData>
      Enrolled: Set<string> }       // set of user IDs (strings) who opted in

// Default empty storage
let emptyStorage =
    { Users = Map.empty
      Enrolled = Set.empty }

// JSON options (readable formatting)
let jsonOptions =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

// Load storage from file (or create if missing)
let loadStorage () =
    try
        if File.Exists(storageFile) then
            let txt = File.ReadAllText(storageFile)
            JsonSerializer.Deserialize<Storage>(txt, jsonOptions)
            |> Option.ofObj
            |> Option.defaultValue emptyStorage
        else
            emptyStorage
    with ex ->
        // If anything goes wrong reading, start fresh but log.
        printfn "Failed to read storage, starting fresh: %s" (ex.Message)
        emptyStorage

// Save storage to file
let saveStorage (s: Storage) =
    try
        let txt = JsonSerializer.Serialize(s, jsonOptions)
        File.WriteAllText(storageFile, txt)
    with ex ->
        printfn "Failed to write storage: %s" (ex.Message)

// -----------------------------
// Thread-safe storage wrapper
// -----------------------------

// We'll keep an in-memory state and protect it by a lock object
let stateLock = obj()
let mutable internalState = loadStorage()

// Helper to safely update the state via a function and persist
let updateState (f: Storage -> Storage) =
    lock stateLock (fun () ->
        internalState <- f internalState
        saveStorage internalState
    )

// Read-only access
let readState () =
    lock stateLock (fun () -> internalState)

// -----------------------------
// Helper money functions
// -----------------------------

// Ensure a user exists in storage
let ensureUser (userId: string) =
    updateState (fun s ->
        if s.Users.ContainsKey(userId) then s
        else
            let ud = { BalanceCents = 0L; RemainderCents = 0.0M }
            { s with Users = s.Users.Add(userId, ud) }
    )

// Add decimal cents to a user, handling remainder accumulation.
// amountDecimal is in cents, e.g., 36.666... means ~36.666 cents
let addFractionalCents (userId: string) (amountDecimal: decimal) =
    lock stateLock (fun () ->
        let s = internalState
        let userKey = userId
        let ud =
            match s.Users.TryGetValue(userKey) with
            | true, v -> v
            | _ -> { BalanceCents = 0L; RemainderCents = 0.0M }

        // add remainder
        let newRemainder = ud.RemainderCents + amountDecimal
        // separate whole cents and leftover fraction
        let wholeToAdd = decimal (Math.Floor(float newRemainder))
        let leftover = newRemainder - wholeToAdd

        // update integer cents
        let newBalance = ud.BalanceCents + int64 wholeToAdd

        let newUd = { BalanceCents = newBalance; RemainderCents = leftover }
        internalState <- { s with Users = s.Users.Add(userKey, newUd) }
        saveStorage internalState
    )

// Get a user's balance as a decimal dollars value for display
let getBalanceDollars (userId: string) =
    let s = readState()
    match s.Users.TryGetValue(userId) with
    | true, ud -> (decimal ud.BalanceCents + ud.RemainderCents) / 100.0M
    | _ -> 0.0M

// -----------------------------
// Background worker: credit enrolled users every minute
// -----------------------------

let startBackgroundCredit (cts: CancellationTokenSource) =
    Task.Run(fun () ->
        async {
            while not cts.IsCancellationRequested do
                try
                    // We credit each enrolled user centsPerMinute per minute.
                    let s = readState()
                    if not (s.Enrolled.IsEmpty) then
                        // Pre-calc the cents to add this tick (decimal cents)
                        let toAdd = centsPerMinute // decimal
                        // Add to every enrolled user
                        for uid in s.Enrolled do
                            // ensure user exists
                            ensureUser uid
                            // add fractional cents safely
                            addFractionalCents uid toAdd

                        printfn "Credited %M cents to %d user(s)" toAdd s.Enrolled.Count
                    else
                        // nothing to do
                        ()

                with ex ->
                    printfn "Error in background credit loop: %s" (ex.Message)

                // Wait for the next tick
                do! Async.Sleep tickMs
        } |> Async.StartAsTask :> Task
    ) |> ignore

// -----------------------------
// Commands: startpay, stoppay, balance
// -----------------------------

type EconomyCommands() =
    inherit BaseCommandModule()

    // Start receiving payments
    [<Command("startpay")>]
    [<Description("Opt in to earn the virtual $22/hour. Usage: !startpay")>]
    member _.StartPay(ctx: CommandContext) =
        task {
            let uid = ctx.User.Id.ToString()
            // add to enrolled set
            updateState (fun s ->
                let s = ensureUser uid; // ensure user record present
                internalState // ensureUser already saved state
            )
            // Now add to enrolled
            updateState (fun s -> { s with Enrolled = s.Enrolled.Add(uid) })
            do! ctx.RespondAsync(sprintf "You are now enrolled for virtual earnings. Use `!balance` to view your balance." ) |> Task.Ignore
        }

    // Stop receiving payments
    [<Command("stoppay")>]
    [<Description("Opt out from earning. Usage: !stoppay")>]
    member _.StopPay(ctx: CommandContext) =
        task {
            let uid = ctx.User.Id.ToString()
            updateState (fun s -> { s with Enrolled = s.Enrolled.Remove(uid) })
            do! ctx.RespondAsync("You have been unsubscribed from virtual earnings.") |> Task.Ignore
        }

    // Check balance
    [<Command("balance")>]
    [<Description("Check your current virtual balance. Usage: !balance")>]
    member _.Balance(ctx: CommandContext) =
        task {
            let uid = ctx.User.Id.ToString()
            ensureUser uid
            let dollars = getBalanceDollars uid
            // Format nicely with 2 decimals
            do! ctx.RespondAsync(sprintf "%s, your balance is $%.2f (virtual currency)." ctx.User.Mention (float dollars)) |> Task.Ignore
        }

// -----------------------------
// Bot startup and main
// -----------------------------

[<EntryPoint>]
let main argv =
    // Read token from environment variable
    let token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
    if String.IsNullOrWhiteSpace token then
        printfn "Please set the DISCORD_TOKEN environment variable and restart."
        1
    else
        // DSharpPlus configuration
        let cfg = DiscordConfiguration(
                    Token = token,
                    TokenType = TokenType.Bot,
                    Intents = DiscordIntents.Guilds ||| DiscordIntents.GuildMessages ||| DiscordIntents.MessageContent
                  )

        use discord = new DiscordClient(cfg)

        // CommandsNext (simple command handler)
        let commandsConfig = CommandsNextConfiguration(StringPrefix = "!")
        let commands = discord.UseCommandsNext(commandsConfig)
        commands.RegisterCommands<EconomyCommands>() |> ignore

        // Graceful shutdown token
        let cts = new CancellationTokenSource()

        // Start the background crediting worker
        startBackgroundCredit cts

        // Register ready handler
        discord.add_Ready (fun _ ->
            printfn "Bot connected and ready!"
            Task.CompletedTask
        )

        // Connect and run until canceled
        task {
            do! discord.ConnectAsync()
            printfn "Bot connected. Press Ctrl+C to exit."

            // Hook up Ctrl+C to cancel, ensuring persistence saved if needed
            Console.CancelKeyPress.Add(fun args ->
                args.Cancel <- true
                printfn "Shutting down..."
                cts.Cancel()
            )

            // Keep running until cancellation requested
            while not cts.IsCancellationRequested do
                do! Task.Delay(1000)
            
            // cleanup
            do! discord.DisconnectAsync()
            0
        } |> Task.WaitAll

        0
