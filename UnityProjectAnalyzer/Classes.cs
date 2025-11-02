using System.Collections.Generic;

namespace UnityProjectAnalyzer;

public class UnityObject
{
    public long FileId;
    public long ParentId = 0;
    public readonly List<long> ChildIds = [];
}

public class GameObject : UnityObject
{
    public string Name = string.Empty;
    public Transform? Transform;
}

public class Transform : UnityObject
{
    public long GameObjectId;
    public GameObject? GameObject;
}

public class MonoBehaviour
{
    public string RelativePath = string.Empty;
    public string Guid = string.Empty;
    public bool IsUsed = false;
}