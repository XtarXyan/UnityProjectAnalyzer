using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using YamlDotNet.RepresentationModel;

namespace UnityProjectAnalyzer;

public partial class SceneParser(ConcurrentDictionary<string, MonoBehaviour> sharedScripts)
{
    // Keyed by FileId. Reset on each Parse() call.
    private readonly Dictionary<long, GameObject> _gameObjects = new();
    private readonly Dictionary<long, Transform> _transforms = new();
    
    // Keyed by GUID. Persists across multiple Parse() calls (shared reference).
    private ConcurrentDictionary<string, MonoBehaviour> Scripts => sharedScripts;

    private readonly StringBuilder _stringBuilder = new();
    private FileInfo? _file;

    public Dictionary<long, GameObject> Parse(FileInfo file)
    { 
        Debug.Assert(file.Exists && file.Extension == ".unity");
        
        // Store the file for logs across multiple methods
        _file = file;
        
        _gameObjects.Clear();
        _transforms.Clear();
        _stringBuilder.Clear();
        
        string? header = null;
        
        using (var streamReader = new StreamReader(file.OpenRead()))
        {
            var lines = streamReader.ReadToEnd().Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);

            // Check each line for a header and store the contents before it
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                if (line.StartsWith("--- "))
                {
                    if (header != null)
                    {
                        var content = _stringBuilder.ToString();
                        ProcessDocument(header, content);
                    }
                    _stringBuilder.Clear();
                    header = line;
                    continue;
                }
                _stringBuilder.AppendLine(line);
            }
        }
        
        // Process the last document
        if (header != null && _stringBuilder.Length > 0)
        {
            var content = _stringBuilder.ToString();
            ProcessDocument(header, content);
        }
        
        LinkHierarchy();
        
        return _gameObjects;
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
                    // If it is a GameObject, we are only interested in the name and the file id
                    // Since GameObjects don't store hierarchy information
                    
                    var objectInfo = new GameObject
                    {
                        Name = value.Children[new YamlScalarNode("m_Name")].ToString(),
                        FileId = long.Parse(match.Groups[2].Value)
                    };
                    
                    return objectInfo;
                }
                case "Transform":
                {
                    // If it is a Transform, we are interested in:
                    // 1. The file ID of the Transform
                    var objectInfo = new Transform
                    {
                        FileId = long.Parse(match.Groups[2].Value)
                    };
                    
                    // 2. The file ID of the GameObject associated with the Transform
                    if (!GetSubnodeProperty(value, "m_GameObject", out long gameObjectFileId))
                    {
                        // This is unexpected, but we can skip this Transform
                        return null;
                    }
                    objectInfo.GameObjectId = gameObjectFileId;
                    
                    // 3. The file ID of the parent (father) Transform (if any)
                    objectInfo.ParentId = GetSubnodeProperty(value, "m_Father", out long parentFileId) ? 
                        parentFileId : 0;

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
                    Scripts.TryAdd(guid, monoBehaviour);
                    
                    // We now know that the script is used in this scene
                    Scripts[guid].IsUsed = true;
                    
                    return null;
                }
            }
            
        }

        return null;
    }
    
    // Helper methods
    private void ProcessDocument(string header, string content)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(content));
    
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
            if (!_gameObjects.TryGetValue(transform.GameObjectId, out var parentGameObject))
            {
                parentGameObject = new GameObject { FileId = transform.GameObjectId };
            }
            transform.GameObject = parentGameObject;
            
            // If it already exists, update the GameObject, parent and child ids since they're likely not yet set
            if (!_transforms.TryAdd(transform.FileId, transform))
            {
                _transforms[transform.FileId].GameObject = transform.GameObject;
                _transforms[transform.FileId].ParentId = transform.ParentId;
                _transforms[transform.FileId].ChildIds.AddRange(transform.ChildIds);
            }
            
            // Set the GameObject to also reference the Transform
            transform.GameObject.Transform = transform;
        }

        // If it is a GameObject, add it to the list of game objects
        // If it already exists, add in the name since it's likely not yet set
        if (unityObject is not GameObject gameObject)
        {
            return;
        }

        if (!_gameObjects.TryAdd(gameObject.FileId, gameObject))
        {
            _gameObjects[gameObject.FileId].Name = gameObject.Name;
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
        foreach (var transform in _transforms.Values)
        {
            transform.GameObject ??= _gameObjects.GetValueOrDefault(transform.GameObjectId);
            transform.GameObject?.Transform = transform;
        }
            
        // 2. Iterate through all GameObjects and link their parents and children
        foreach (var gameObject in _gameObjects.Values)
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
                gameObject.ParentId = _transforms.GetValueOrDefault(transform.ParentId)?.GameObject?.FileId ?? 0;
            }

            if (transform.ChildIds.Count == 0)
            {
                continue;
            }
                
            // Add children
            foreach (var childId in transform.ChildIds)
            {
                var childNode = _transforms.GetValueOrDefault(childId)?.GameObject;
                if (childNode != null)
                {
                    gameObject.ChildIds.Add(childNode.FileId);
                }
            }
                
        }
    }
}