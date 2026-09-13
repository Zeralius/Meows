using Meows.Kitten;

// Kitten: makes another one.
//
//   dotnet run --project Meows.Kitten -- Whiskers --plain "Print queue" --icon 🧵 --category group.disk
//
// Writes a plugin project the way the fifteen before it were written by hand, adds it to the
// solution, the test project, the list of shipped plugins and the README, then builds it and
// runs the tests that know about every plugin. A generator that falls behind the thing it
// generates is worse than none, so the build is not optional: pass --no-verify to skip it and
// own the consequences.

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

var repo = Repo.Find(Directory.GetCurrentDirectory());
if (repo is null)
{
    Console.Error.WriteLine("Kitten has to be run from inside the Meows checkout: Meows.sln was not found above the current folder.");
    return 2;
}

var litter = new Litter(repo, options);

var refusal = litter.Check();
if (refusal is not null)
{
    Console.Error.WriteLine(refusal);
    return 2;
}

if (options.DryRun)
{
    Console.WriteLine("Would write:");
    foreach (var path in litter.FilesToWrite())
        Console.WriteLine($"  {path}");
    Console.WriteLine("Would edit:");
    foreach (var path in litter.FilesToEdit())
        Console.WriteLine($"  {path}");
    return 0;
}

var written = litter.Write();
Console.WriteLine("Written:");
foreach (var path in written)
    Console.WriteLine($"  {path}");

var edited = litter.Edit();
Console.WriteLine("Edited:");
foreach (var path in edited)
    Console.WriteLine($"  {path}");

if (!options.Verify)
{
    Console.WriteLine();
    Console.WriteLine("Not built, because --no-verify. Build Meows.Tests before trusting any of it.");
    Console.WriteLine(litter.WhatToOpenFirst());
    return 0;
}

Console.WriteLine();
Console.WriteLine("Building it, so a stale template fails here and not at release time.");
var verdict = Verify.Run(repo, options.Name);
if (verdict is not null)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(verdict);
    Console.Error.WriteLine("The files are still there. Fix what it says, or remove the folder and the four edits by hand.");
    return 1;
}

Console.WriteLine();
Console.WriteLine($"{options.Name} builds and the plugin-wide tests pass.");
Console.WriteLine(litter.WhatToOpenFirst());
return 0;
