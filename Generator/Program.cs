//BEGIN_FILE HFT/Generator/Program.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace Generator;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: Generator <ProjectPath> <IntermediateOutputPath> <ProjectName>");
            return;
        }

        string projectDir = args[0];
        string objDir = args[1];
        string projectName = args[2];

        // Filename matches HFT.targets exactly
        string outputFile = Path.Combine(objDir, $"{projectName}JsonContext.g.cs");

        Regex namespaceRegex = new Regex(@"namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);

        // Allow '<', '>', and ',' in the capture group to grab generics
        Regex typeRegex = new Regex(@"\[RegisterJson\][\s\S]*?(?:record\s+struct|record\s+class|class|struct|enum|record)\s+([A-Za-z0-9_<>,\s]+)", RegexOptions.Compiled);

        HashSet<string> registeredTypes = new HashSet<string>();

        // Safe Directory Traversal: Prevents UnauthorizedAccessException
        Queue<string> directories = new Queue<string>();
        directories.Enqueue(projectDir);

        while (directories.Count > 0)
        {
            string currentDir = directories.Dequeue();

            try
            {
                foreach (string dir in Directory.GetDirectories(currentDir))
                {
                    string dirName = Path.GetFileName(dir);
                    if (dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        dirName.StartsWith("."))
                    {
                        continue;
                    }
                    directories.Enqueue(dir);
                }
            }
            catch (UnauthorizedAccessException) { /* Ignore inaccessible directories */ }
            catch (DirectoryNotFoundException) { /* Ignore deleted directories */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[JsonContextGenerator] Error reading directories in {currentDir}: {ex.Message}");
            }

            try
            {
                foreach (string file in Directory.GetFiles(currentDir, "*.cs"))
                {
                    try
                    {
                        string content = File.ReadAllText(file);
                        if (!content.Contains("[RegisterJson]"))
                        {
                            continue;
                        }

                        Match nsMatch = namespaceRegex.Match(content);
                        string fileNamespace = nsMatch.Success ? nsMatch.Groups[1].Value : projectName;

                        foreach (Match match in typeRegex.Matches(content))
                        {
                            // Strip any whitespace that might have been captured inside < >
                            string typeName = match.Groups[1].Value.Trim().Replace(" ", "");

                            // Convert open generic signatures (e.g. Header<T> or Map<K,V>) to typeof-friendly syntax (Header<> or Map<,>)
                            if (typeName.Contains("<"))
                            {
                                int commaCount = typeName.Split(',').Length - 1;
                                string unboundBrackets = "<" + new string(',', commaCount) + ">";
                                typeName = typeName.Substring(0, typeName.IndexOf('<')) + unboundBrackets;
                            }

                            registeredTypes.Add(typeName);
                        }
                    }
                    catch (IOException) { /* Ignore locked files during build */ }
                    catch (UnauthorizedAccessException) { /* Ignore inaccessible files */ }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[JsonContextGenerator] Error processing file {file}: {ex.Message}");
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* Ignore inaccessible directory contents */ }
            catch (DirectoryNotFoundException) { /* Ignore deleted directories */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[JsonContextGenerator] Error reading files in {currentDir}: {ex.Message}");
            }
        }

        // 2. Abort completely if nothing is tagged
        if (registeredTypes.Count == 0)
        {
            if (File.Exists(outputFile))
            {
                try { File.Delete(outputFile); } catch { }
                Console.WriteLine($"[JsonContextGenerator] Cleaned up unused AOT Context for {projectName}.");
            }
            return;
        }

        string safeProjectName = projectName.Replace(".", "_").Replace("-", "_");

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine("using Tools;");
        sb.AppendLine();

        // 3. Class lives in the same namespace as the current Project
        sb.AppendLine($"namespace {safeProjectName};");
        sb.AppendLine();
        sb.AppendLine("[JsonSourceGenerationOptions(UseStringEnumConverter = true)]");

        foreach (string type in registeredTypes)
        {
            sb.AppendLine($"[JsonSerializable(typeof({type}))]");
        }

        sb.AppendLine($"public sealed partial class {safeProjectName}JsonContext : JsonSerializerContext");
        sb.AppendLine("{");
        sb.AppendLine("\t[System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("\tinternal static void Register()");
        sb.AppendLine("\t{");
        sb.AppendLine("\t\tJson.RegisterContext(Default);");
        sb.AppendLine("\t}");
        sb.AppendLine("}");

        try
        {
            Directory.CreateDirectory(objDir);
            string newContent = sb.ToString();

            if (!File.Exists(outputFile) || File.ReadAllText(outputFile) != newContent)
            {
                File.WriteAllText(outputFile, newContent);
                Console.WriteLine($"[JsonContextGenerator] Generated AOT Context for {projectName} with {registeredTypes.Count} types.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JsonContextGenerator] Failed to write file: {ex.Message}");
        }
    }
}
//END_FILE HFT/Generator/Program.cs