using Proxy;
using System;
using System.Runtime.InteropServices;

Console.WriteLine("--- SYSTEM CHECK ---");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine("--------------------");

Console.WriteLine("Running Proxy");

string input = args.Length > 0 ? args[0].ToLower() : "";
while (input != "server" && input != "client")
{
    Console.WriteLine("type 'server' or 'client'");
    string? readInput = Console.ReadLine();
    if (readInput != null)
    {
        input = readInput.ToLower();
    }
}

if (input == "server")
{
    ServerScenario serverScenario = new ServerScenario("Proxy");
    serverScenario.Start();
}

if (input == "client")
{
    ClientScenario clientScenario = new ClientScenario("Proxy");
    clientScenario.Start();
}






