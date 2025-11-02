using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace UnityProjectAnalyzer;

public partial class SceneParser
{
    // Keyed by FileId. Reset on each Parse() call.
    private static readonly Dictionary<long, GameObject> GameObjects = new();
    private static readonly Dictionary<long, Transform> Transforms = new();
    
    // Keyed by GUID. Persists across multiple Parse() calls.
    private readonly Dictionary<string, MonoBehaviour> _scripts = new();
    public IDictionary<string, MonoBehaviour> Scripts => _scripts;

    private static readonly StringBuilder StringBuilder = new();
    private static FileInfo? _file;

    public Dictionary<long, GameObject> Parse(FileInfo file)
    { 
        Debug.Assert(file.Exists && file.Extension == ".unity");
        
        // Store the file for logs across multiple methods
        _file = file;
        
        GameObjects.Clear();
        Transforms.Clear();
        StringBuilder.Clear();
        
        string? header = null;
        
        var streamReader = new StreamReader(file.OpenRead());
        var lines = streamReader.ReadToEnd().Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);
        streamReader.Close();

        // Check each line for a header and store the contents before it
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            if (line.StartsWith("--- "))
            {
                if (header != null)
                {
                    ProcessDocument(header);
                }
                StringBuilder.Clear();
                header = line;
                continue;
            }
            StringBuilder.AppendLine(line);
        }
        
        streamReader.Close();
        
        // Process the last document
        if (header != null && StringBuilder.Length > 0)
        {
            ProcessDocument(header);
        }
        
        LinkHierarchy();
        
        return GameObjects;
    }
    
    // Regular expression that loosely matches the header of a Unity document
    [GeneratedRegex(@"---\s*!u!\s*(\d+)\s*&\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex HeaderRegex();
    
    private UnityObject? ParseDocument(YamlDocument doc, string header)
    {
        var match = HeaderRegex().Match(header);
        if (!match.Success)
        {
            Console.WriteLine("Warning: Failed to parse header: " + header + " in file " + _file?.Name);
            return null;
        }
        
        
        if (doc.RootNode is not YamlMappingNode root)
        {
            return null;
        }
        
        foreach (var (keyNode, valueNode) in root.Children)
        {
            if (keyNode is not YamlScalarNode scalarNode)
            {
                continue;
            }
            
            var value = (YamlMappingNode) valueNode;
        
            switch (scalarNode.Value)
            {
                case "GameObject":
                {
                    var objectInfo = new GameObject();
                    
                    // If it is a GameObject, we are only interested in the name and the file id
                    // Since GameObjects don't store hierarchy information
                    
                    objectInfo.Name = value.Children["m_Name"].ToString();
                    objectInfo.FileId = long.Parse(match.Groups[2].Value);
                    
                    return objectInfo;
                }
                case "Transform":
                {
                    var objectInfo = new Transform();
                    
                    // If it is a Transform, we are interested in:
                    // 1. The file ID of the Transform
                    objectInfo.FileId = long.Parse(match.Groups[2].Value);
                    
                    // 2. The file ID of the GameObject associated with the Transform
                    if (!GetSubnodeProperty(value, "m_GameObject", out long gameObjectFileId))
                    {
                        return null;
                    }
                    objectInfo.GameObjectId = gameObjectFileId;
                    
                    // 3. The file ID of the parent (father) Transform (if any)
                    if (!GetSubnodeProperty(value, "m_Father", out long parentFileId))
                    {
                        objectInfo.ParentId = 0;
                    }
                    else
                    {
                        objectInfo.ParentId = parentFileId;
                    }

                    // 4. The file IDs of the children Transforms (if any)
                    if (value.Children["m_Children"] is not YamlSequenceNode childrenSequence)
                    {
                        return null;
                    }
                    
                    foreach (var child in childrenSequence.Children)
                    {
                        if (child is YamlMappingNode childMapping &&
                            childMapping.Children.TryGetValue(new YamlScalarNode("fileID"), out var fileIdNode))
                        {
                            var childId = long.TryParse(fileIdNode.ToString(), out var id) ? id : 0;
                            objectInfo.ChildIds.Add(childId);
                        }

                    }
                    return objectInfo;
                }
                case "MonoBehaviour":
                {
                    var monoBehaviour = new MonoBehaviour();
                    
                    // Get the Guid of the script
                    if (GetSubnodeProperty(value, "m_Script", out string guid, "guid"))
                    {
                        monoBehaviour.Guid = guid;
                    }
                    
                    // Add the script to the dictionary if it doesn't already exist
                    _scripts.TryAdd(guid, monoBehaviour);
                    
                    // We now know that the script is used in this scene
                    _scripts[guid].IsUsed = true;
                    
                    return null;
                }
            }
            
        }

        return null;
    }
    
    // Helper methods
    private void ProcessDocument(string header)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(StringBuilder.ToString()));
    
        // Parse the yaml document and determine whether it is a GameObject or a Transform
        var unityObject = ParseDocument(yaml.Documents[0], header);
        if (unityObject == null)
        {
            return;
        }

        // If it is a Transform, add it to the list of transforms
        if (unityObject is Transform transform)
        {
            // Warn if the Transform is not attached to any GameObject (id is 0)
            if (transform.GameObjectId == 0)
            {
                Console.WriteLine("Warning: Transform " + transform.FileId + " in file " + _file?.Name +
                                  " is not attached to any GameObject." + 
                                  " This is likely due to a broken reference in the scene file.");
                return;
            }
            
            // Get the GameObject associated with the Transform if it exists
            if (!GameObjects.TryGetValue(transform.GameObjectId, out var parentGameObject))
            {
                parentGameObject = new GameObject { FileId = transform.GameObjectId };
            }
            transform.GameObject = parentGameObject;
            
            // If it already exists, update the GameObject, parent and child ids since they're likely not yet set
            if (!Transforms.TryAdd(transform.FileId, transform))
            {
                Transforms[transform.FileId].GameObject = transform.GameObject;
                Transforms[transform.FileId].ParentId = transform.ParentId;
                Transforms[transform.FileId].ChildIds.AddRange(transform.ChildIds);
            }
            
            // Set the GameObject to also reference the Transform
            transform.GameObject?.Transform = transform;
        }

        // If it is a GameObject, add it to the list of game objects
        // If it already exists, add in the name since it's likely not yet set
        if (unityObject is GameObject gameObject)
        {
            if (!GameObjects.TryAdd(gameObject.FileId, gameObject))
            {
                GameObjects[gameObject.FileId].Name = gameObject.Name;
            }
        }
    }
    
    // Gets the sub-property from a structure such as
    // Node:
    // |
    // -- subnode: {subkey: 1234}
    private static bool GetSubnodeProperty(YamlMappingNode node, string key, out long value, string subkey="fileID")
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var subnode)
            && subnode is YamlMappingNode subMappingNode
            && subMappingNode.Children.TryGetValue(new YamlScalarNode(subkey), out var fileIdNode))
        { 
            value = long.TryParse(fileIdNode.ToString(), out var id) ? id : 0;
            return true;
        }
        value = 0;
        return false;
    }
    
    // Overload for string values
    private static bool GetSubnodeProperty(YamlMappingNode node, string key, out string value, string subkey="fileID")
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var subnode)
            && subnode is YamlMappingNode subMappingNode
            && subMappingNode.Children.TryGetValue(new YamlScalarNode(subkey), out var fileIdNode))
        {
            value = fileIdNode.ToString();
            return true;
        }
        
        value = string.Empty;
        return false;
    }
    
    private void LinkHierarchy()
    {
        // 1. Iterate through all Transforms and link them to their GameObjects and the other way around
        foreach (var transform in Transforms.Values)
        {
            transform.GameObject ??= GameObjects.GetValueOrDefault(transform.GameObjectId);
            transform.GameObject?.Transform = transform;
        }
        
        // 2. Iterate through all GameObjects and link their parents and children
        foreach (var gameObject in GameObjects.Values)
        {
            // If a GameObject has a transform, mirror the transform's relations
            var transform = gameObject.Transform;
            if (transform == null)
            {
                continue;
            }

            // Add parent
            if (transform.ParentId != 0)
            {
                gameObject.ParentId = Transforms.GetValueOrDefault(transform.ParentId)?.GameObject?.FileId ?? 0;
            }

            if (transform.ChildIds.Count == 0)
            {
                continue;
            }
            
            // Add children
            foreach (var childId in transform.ChildIds)
            {
                var childNode = Transforms.GetValueOrDefault(childId)?.GameObject;
                if (childNode != null)
                {
                    gameObject.ChildIds.Add(childNode.FileId);
                }
            }
            
        }
    }
}