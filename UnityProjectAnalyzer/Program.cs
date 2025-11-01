using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UnityProjectAnalyzer;

internal static partial class Program
{
    private static readonly SceneParser SceneParser = new SceneParser();
    
    private static void Main(string[] args)
    {
        // Get the name of the executable for output
        var exePath = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
        
        // Args and options
        Argument<DirectoryInfo> inputDirectoryArgument = new("project directory")
        {
            Description = "Root directory of your Unity project.",
            DefaultValueFactory = parseResult => new DirectoryInfo("./")
            
        };
        Argument<DirectoryInfo> outputDirectoryArgument = new("output directory")
        {
            Description = "Directory the output will be dumped to.",
            DefaultValueFactory = parseResult => new DirectoryInfo("./")
        };
        
        // Bind args to the root command
        RootCommand rootCommand = new(
            "Unity Project Analyzer"
        );
        
        rootCommand.Arguments.Add(inputDirectoryArgument);
        rootCommand.Arguments.Add(outputDirectoryArgument);
        
        rootCommand.SetAction(parseResult =>
        {
            try
            {
                if (parseResult.Errors.Count > 0)
                {
                    Console.WriteLine("Usage: " + exePath + " <input directory> <output directory> [options]");
                    return;
                }

                var inputDirectoryPath = parseResult.GetValue(inputDirectoryArgument)?.FullName;
                if (inputDirectoryPath == null)
                {
                    return;
                }

                var inputDirectory = new DirectoryInfo(inputDirectoryPath);

                var outputDirectoryPath = parseResult.GetValue(outputDirectoryArgument)?.FullName;
                if (outputDirectoryPath == null)
                {
                    return;
                }

                var outputDirectory = new DirectoryInfo(outputDirectoryPath);

                CheckFiles(inputDirectory, outputDirectory);
            }
            catch (Exception e)
            {
                if (e is DirectoryNotFoundException)
                {
                    Console.WriteLine("Directory not found: " + e.Message);
                    return;
                }
                
                Console.WriteLine(e);
            }
        });
        
        // Parse args
        var parseResult = rootCommand.Parse(args);
        
        parseResult.Invoke();
    }

    private static void CheckFiles(DirectoryInfo inputDirectory, DirectoryInfo outputDirectory)
    {
        if (!inputDirectory.Exists)
        {
            throw new DirectoryNotFoundException();
        }
        
        // Find all .unity scene files
        foreach (var file in inputDirectory.EnumerateFiles("*.unity"))
        {
            // Create a new output file
            var outputFile = new FileInfo(Path.Combine(outputDirectory.FullName, file.Name + ".dump"));
            outputFile.Directory?.Create();
            
            using var outputFileStream = outputFile.Create();
            using var writer = new StreamWriter(outputFileStream);
            
            // Parse the scene file and dump the hierarchy to the output file
            var hierarchy = SceneParser.Parse(file);
            
            foreach (var (key, value) in hierarchy)
            {
                if (value.ParentId == 0)
                {
                    DumpScene(hierarchy, key, writer);
                }
            }
            
        }
        
        // Walk through the inputDirectory tree and recurse into subdirectories
        foreach (var subDirectory in inputDirectory.EnumerateDirectories())
        {
            CheckFiles(subDirectory, outputDirectory);
        }
        
    }

    [GeneratedRegex(@"([a-z])([A-Z])|([A-Z])([A-Z][a-z])|([a-zA-Z])(\d)", RegexOptions.Compiled)]
    private static partial Regex FormatterRegex();

    private static void DumpScene(
        Dictionary<long, GameObject> hierarchy, long rootKey, StreamWriter writer, int level = 0)
    {
        Console.WriteLine("Outputting key: " + rootKey);
        if (!hierarchy.TryGetValue(rootKey, out var rootObject))
        {
            return;
        }
        
        // Indent based on level
        var indent = new string('-', level * 2);
        var name = rootObject.Name;
        
        // Turn PascalCaseName1 into Pascal Case Name 1
        name = FormatterRegex().Replace(name, "$1$3$5 $2$4$6");
        
        // Write the GameObject info to the output file
        writer.WriteLine($"{indent}{name}");
        
        // Recurse into children
        foreach (var childId in rootObject.ChildIds)
        {
            DumpScene(hierarchy, childId, writer, level + 1);
        }
    }
}