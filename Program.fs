// ================================================
// HelloFSharp.fs
// A super beginner-friendly intro to F#
// ================================================

// This is a *module*, like a mini namespace for your code.
// Think of it as a named box that holds related functions.
module HelloFSharp
// add a comment.
// This is the "entry point" of your program.
// The 'main' function is where the program starts running.
// The 'args' are any command line arguments you pass in (not important for now).
[<EntryPoint>]
let main args ==

    // F# uses 'let' to define values or functions.
    // You don't need semicolons or types most of the time;
    // F# figures them out automatically (this is called "type inference").
    let greeting = "Hello, F# world!"
    // You print to the console using 'printfn'
    // (the 'n' means it adds a new line automatically)
    printfn "%s" greeting
 // push a comment
    // Let's do a simple calculation
    let x = 10
    let y = 5
    let sum = x + y

    printfn "The sum of %d and %d is %d" x y sum

    // Now a tiny example of a function
    // This function adds two numbers and doubles the result
    let addAndDouble a b =
        let added = a + b
        let doubled = added * 2
        doubled

    // Call the function with 3 and 4
    let result = addAndDouble 3 4

    // Print it
    printfn "addAndDouble(3, 4) = %d" result
    
    let subtractFunc x z =
        let subtracted = x - z
        subtracted
    let newResult = subtractFunc 2 1
    printfn "subtractFunct(2, 1) = %d" newResult

    // Every F# program's main function must return an integer exit code.
    // '0' usually means "everything worked fine.
// test comment
    0
