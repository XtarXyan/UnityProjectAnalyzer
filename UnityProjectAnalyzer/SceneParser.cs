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
    private readonly Dictionary<long, GameObject> _gameObjects = new();
    private readonly Dictionary<long, Transform> _transforms = new();
    
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
        
        Console.WriteLine("Parsing " + file.FullName);
        
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
                _stringBuilder.Clear();
                header = line;
                continue;
            }
            _stringBuilder.AppendLine(line);
        }
        
        streamReader.Close();
        
        // Process the last document
        if (header != null && _stringBuilder.Length > 0)
        {
            ProcessDocument(header);
        }
        
        LinkHierarchy();

        /*
        Console.WriteLine("GameObjects:");
        foreach (var gameObject in _gameObjects.Values)
        {
            Console.WriteLine("--GameObject:");
            Console.WriteLine("Name: " + gameObject.Name);
            Console.WriteLine("FileId: " + gameObject.FileId);
            Console.WriteLine("ParentId: " + gameObject.ParentId);
            Console.WriteLine("ChildIds: " + string.Join(", ", gameObject.ChildIds));
            if (gameObject.Transform != null)
            {
                Console.WriteLine("Transform FileId: " + gameObject.Transform.FileId);
                Console.WriteLine("Transform ParentId: " + gameObject.Transform.ParentId);
                Console.WriteLine("Transform ChildIds: " + string.Join(", ", gameObject.Transform.ChildIds));
            }
            else
            {
                Console.WriteLine("Transform: null");           
            }
        }*/
        
        return _gameObjects;
    }
    
    // Regular expression that loosely matches the header of a Unity document
    [GeneratedRegex(@"---\s*!u!\s*(\d+)\s*&\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex HeaderRegex();
    
    private static UnityObject? ParseDocument(YamlDocument doc, string header)
    {
        var match = HeaderRegex().Match(header);
        if (!match.Success)
        {
            Console.WriteLine("Warning: Failed to parse header: " + header);
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
                    if (!GetSubnodeFileId(value, "m_GameObject", out var gameObjectFileId))
                    {
                        return null;
                    }
                    objectInfo.GameObjectId = gameObjectFileId;
                    
                    // 3. The file ID of the parent (father) Transform (if any)
                    if (!GetSubnodeFileId(value, "m_Father", out var parentFileId))
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
            }
            
        }

        return null;
    }
    
    // Helper methods
    private void ProcessDocument(string header)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(_stringBuilder.ToString()));
    
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
            transform.GameObject?.Transform = transform;
        }

        // If it is a GameObject, add it to the list of game objects
        // If it already exists, add in the name since it's likely not yet set
        if (unityObject is GameObject gameObject)
        {
            if (!_gameObjects.TryAdd(gameObject.FileId, gameObject))
            {
                _gameObjects[gameObject.FileId].Name = gameObject.Name;
            }
        }
    }
    
    // Gets the file ID from a structure such as
    // Node:
    // |
    // -- subNode: {fileID: 1234}
    private static bool GetSubnodeFileId(YamlMappingNode node, string key, out long fileId)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var subNode)
            && subNode is YamlMappingNode subMappingNode
            && subMappingNode.Children.TryGetValue(new YamlScalarNode("fileID"), out var fileIdNode))
        { 
            fileId = long.TryParse(fileIdNode.ToString(), out var id) ? id : 0;
            return true;
        }
        fileId = 0;
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