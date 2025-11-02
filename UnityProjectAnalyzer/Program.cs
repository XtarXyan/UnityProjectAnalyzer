using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using YamlDotNet.RepresentationModel;

using CsvHelper;
using CsvHelper.Configuration.Attributes;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityProjectAnalyzer;

internal static partial class Program
{
    private static readonly SceneParser SceneParser = new();
    private static readonly Dictionary<ClassDeclarationSyntax, bool> ClassesReferenced = [];
    private static readonly List<SyntaxTree> SyntaxTrees = [];
    
    private static CSharpCompilation? _compilation;
    private static string _inputDirectoryPath = string.Empty;
    private static string _outputDirectoryPath = string.Empty;
    
    
    
    
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

                _inputDirectoryPath = parseResult.GetValue(inputDirectoryArgument)?.FullName ?? exePath;
                var inputDirectory = new DirectoryInfo(_inputDirectoryPath);

                _outputDirectoryPath = parseResult.GetValue(outputDirectoryArgument)?.FullName ?? exePath;
                var outputDirectory = new DirectoryInfo(_outputDirectoryPath);

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
        
        // Find all .cs files
        foreach (var file in inputDirectory.EnumerateFiles("*.cs"))
        {
            MonoBehaviour script = new();
            
            script.RelativePath = Path.GetRelativePath(_inputDirectoryPath, file.FullName);
            
            var metaPath = inputDirectory.GetFiles(file.Name + ".meta")[0].FullName;
            var yaml = new YamlStream();
            using var reader = new StreamReader(metaPath);
            yaml.Load(reader);
            
            var mapping = (YamlMappingNode)yaml.Documents[0].RootNode;
            foreach (var entry in mapping.Children)
            {
                if (entry.Key.ToString() == "guid")
                {
                    script.Guid = entry.Value.ToString();
                    break;
                }
            }
            if (!SceneParser.Scripts.TryAdd(script.Guid, script))
            {
                // If the script already exists, check if the paths are the same
                if (SceneParser.Scripts[script.Guid].RelativePath == script.RelativePath)
                {
                    continue;
                }
                
                // If not, check if one of the paths is empty (meaning it was added when parsing a scene)
                if (SceneParser.Scripts[script.Guid].RelativePath == string.Empty)
                {
                    SceneParser.Scripts[script.Guid] = script;
                    continue;
                }
                
                // Otherwise, there are two different scripts with the same GUID so something is very wrong
                throw new Exception($"Two instances of GUID {script.Guid} found in your project: " +
                                    $"{SceneParser.Scripts[script.Guid].RelativePath} and {script.RelativePath}");
            }
            
            // Parse the script and add it to the syntax trees collection
            var code = new StreamReader(file.FullName).ReadToEnd();
            var tree = CSharpSyntaxTree.ParseText(code, path: file.FullName);
            SyntaxTrees.Add(tree);
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
        
        // Analyze all collected scripts
        if (SyntaxTrees.Count > 0)
        {
            _compilation = CSharpCompilation.Create("UnityProjectAnalysis")
                .AddReferences(
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                )
                .AddSyntaxTrees(SyntaxTrees);
            
            AnalyzeScripts();
        }
        
        // Dump unused scripts to a CSV file
        var unusedScriptsOutputFile = new FileInfo(Path.Combine(outputDirectory.FullName, "UnusedScripts.csv"));
        unusedScriptsOutputFile.Directory?.Create();
        using var unusedScriptsOutputFileStream = unusedScriptsOutputFile.Create();
        using var unusedScriptsWriter = new StreamWriter(unusedScriptsOutputFileStream);
        DumpUnusedScripts(unusedScriptsWriter);
    }

    [GeneratedRegex(@"([a-z])([A-Z])|([A-Z])([A-Z][a-z])|([a-zA-Z])(\d)", RegexOptions.Compiled)]
    private static partial Regex FormatterRegex();

    private static void DumpScene(
        Dictionary<long, GameObject> hierarchy, long rootKey, StreamWriter writer, int level = 0)
    {
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
    
    private class ScriptRecord(string relativePath, string guid)
    {
        [Name("Relative Path")]
        public string RelativePath => relativePath;
        [Name("GUID")]
        public string Guid => guid;
    }

    private static void DumpUnusedScripts(StreamWriter writer)
    {
        // Iterate through ClassesReferenced and mark scripts as used if their class is referenced
        foreach (var (classDeclaration, isReferenced) in ClassesReferenced)
        {
            if (!isReferenced)
            {
                continue;
            }

            // Find the script that contains this class
            var syntaxTree = classDeclaration.SyntaxTree;
            var filePath = syntaxTree.FilePath;
            var relativePath = Path.GetRelativePath(_inputDirectoryPath, filePath);
            
            // Find the script in SceneParser.Scripts
            var script = SceneParser.Scripts.Values
                .FirstOrDefault(s => s.RelativePath == relativePath);
            
            script?.IsUsed = true;
        }
        
        
        var scriptRecords = SceneParser.Scripts.Values
            .Where(script => !script.IsUsed)
            .Select(script => new ScriptRecord(script.RelativePath, script.Guid))
            .ToList();
        
        using var csvWriter = new CsvWriter(writer, System.Globalization.CultureInfo.InvariantCulture);
        csvWriter.WriteRecords(scriptRecords);
    }

    private static void AnalyzeScripts()
    {
        if (_compilation == null)
        {
            return;
        }
        
        // Iterate through all syntax trees in the compilation
        foreach (var tree in SyntaxTrees)
        {
            var root = tree.GetRoot();
            var model = _compilation.GetSemanticModel(tree);
            
            // Iterate through class declarations
            foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var fields = classDeclaration.Members.OfType<FieldDeclarationSyntax>();
                foreach (var field in fields)
                {
                    // Check if field has [SerializeField] attribute or is public
                    var isSerialized = field.AttributeLists
                        .SelectMany(attrList => attrList.Attributes)
                        .Any(attr => 
                        {
                            var name = attr.Name.ToString();
                            return name == "SerializeField" || name == "UnityEngine.SerializeField" || name == "SerializeFieldAttribute";
                        });
                    var isPublic = field.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword));
                    if (!(isSerialized || isPublic))
                    {
                        continue;
                    }

                    // get the type symbol (or skip if it's not a named type)
                    if (model.GetTypeInfo(field.Declaration.Type).Type is not INamedTypeSymbol fieldTypeSymbol)
                    {
                        continue;
                    }
                    
                    // Get the syntax reference for the type
                    var fieldTypeDeclaration = fieldTypeSymbol
                        .DeclaringSyntaxReferences
                        .Select(sr => sr.GetSyntax())
                        .OfType<ClassDeclarationSyntax>()
                        .FirstOrDefault();
                    
                    if (fieldTypeDeclaration == null || fieldTypeDeclaration == classDeclaration)
                    {
                        continue;
                    }
                    
                    // Mark the referenced class as used
                    ClassesReferenced[fieldTypeDeclaration] = true;
                }
                
                // Add class to the dictionary with a false value if not already present
                ClassesReferenced.TryAdd(classDeclaration, false);
            }
        }
    }
}