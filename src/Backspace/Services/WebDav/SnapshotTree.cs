using System;
using System.Collections.Generic;

namespace Backspace.Services.WebDav;

/// <summary>
/// A whole snapshot's folder tree, held in memory for the life of a mount
/// (U158). A snapshot never changes, so one engine listing answers every
/// later directory listing, look-up and Explorer "calculating size" sweep
/// without asking the engine again. Each node keeps only its own name and a
/// parent link; full paths are put together only for the entries a listing
/// returns. Children are sorted case-insensitively, which is the order
/// Windows enumerates in and lets a look-up binary-search them.
/// </summary>
public sealed class SnapshotTree
{
    public sealed class Node
    {
        internal Node(string name, Node? parent, bool isDirectory, long size, DateTime modifiedUtc)
        {
            Name = name; Parent = parent; IsDirectory = isDirectory; SizeBytes = size; ModifiedUtc = modifiedUtc;
        }

        public string Name { get; }
        public Node? Parent { get; }
        public bool IsDirectory { get; }
        public long SizeBytes { get; }
        public DateTime ModifiedUtc { get; internal set; }
        internal List<Node>? Building;
        private Node[] _children = Array.Empty<Node>();
        public IReadOnlyList<Node> Children => _children;

        /// <summary>Source-relative path with forward slashes, "" for the root.</summary>
        public string Path
        {
            get
            {
                if (Parent is null) return "";
                if (Parent.Parent is null) return Name;
                return Parent.Path + "/" + Name;
            }
        }

        public Node? Child(string name)
        {
            int lo = 0, hi = _children.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                var c = Compare(_children[mid].Name, name);
                if (c == 0) return _children[mid];
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return null;
        }

        /// <summary>Index of the first child named after <paramref name="marker"/>
        /// in enumeration order, for resuming a directory listing.</summary>
        public int IndexAfter(string marker)
        {
            int lo = 0, hi = _children.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (Compare(_children[mid].Name, marker) <= 0) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        internal void Seal()
        {
            if (Building is null) return;
            var array = Building.ToArray();
            Array.Sort(array, (a, b) => Compare(a.Name, b.Name));
            _children = array;
            Building = null;
        }
    }

    /// <summary>Windows' enumeration and look-up order for this read-only volume.</summary>
    public static int Compare(string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

    private SnapshotTree(Node root, int files, int directories, long bytes)
    {
        Root = root; FileCount = files; DirectoryCount = directories; TotalBytes = bytes;
    }

    public Node Root { get; }
    public int FileCount { get; }
    public int DirectoryCount { get; }
    public long TotalBytes { get; }

    /// <summary>The node at a source-relative path (either slash), or null.</summary>
    public Node? Find(string relativePath)
    {
        var node = Root;
        if (string.IsNullOrEmpty(relativePath)) return node;
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment.Length == 0) continue;
            if (!node.IsDirectory) return null;
            node = node.Child(segment);
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Builds the tree from the engine's flat listing. Parents may
    /// arrive after their children; a missing parent is created as a folder.</summary>
    public static SnapshotTree Build(IEnumerable<RevisionTreeNode> nodes)
    {
        var root = new Node("", null, isDirectory: true, 0, DateTime.MinValue) { Building = new() };
        var directories = new Dictionary<string, Node>(StringComparer.Ordinal) { [""] = root };
        int files = 0;
        long bytes = 0;

        Node Directory(string path)
        {
            if (directories.TryGetValue(path, out var existing)) return existing;
            var slash = path.LastIndexOf('/');
            var parent = Directory(slash < 0 ? "" : path[..slash]);
            var created = new Node(slash < 0 ? path : path[(slash + 1)..], parent, true, 0, DateTime.MinValue) { Building = new() };
            parent.Building!.Add(created);
            directories[path] = created;
            return created;
        }

        foreach (var entry in nodes)
        {
            var path = entry.Path.Trim('/');
            if (path.Length == 0)
            {
                root.ModifiedUtc = entry.ModifiedUtc;
                continue;
            }
            if (entry.IsDirectory)
            {
                Directory(path).ModifiedUtc = entry.ModifiedUtc;
                continue;
            }
            var slash = path.LastIndexOf('/');
            var parent = Directory(slash < 0 ? "" : path[..slash]);
            parent.Building!.Add(new Node(slash < 0 ? path : path[(slash + 1)..], parent, false, entry.SizeBytes, entry.ModifiedUtc));
            files++;
            bytes += entry.SizeBytes;
        }
        foreach (var directory in directories.Values) directory.Seal();
        return new SnapshotTree(root, files, directories.Count - 1, bytes);
    }
}
