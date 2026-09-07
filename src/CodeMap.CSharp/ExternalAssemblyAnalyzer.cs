using System.Reflection.Metadata;
using CodeMap.Core.Ids;
using CodeMap.Core.Models;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace CodeMap.CSharp;












internal static class ExternalAssemblyAnalyzer
{






    internal static string BuildProjectName(PEFile peFile)
    {
        var identity = peFile.Metadata.GetAssemblyDefinition();
        var name = peFile.Metadata.GetString(identity.Name);
        var version = identity.Version;
        var publicKeyToken = FormatPublicKeyToken(peFile);
        var mvid = peFile.Metadata.GetGuid(peFile.Metadata.GetModuleDefinition().Mvid);
        return $"external:{name}@{version}~{publicKeyToken}#{mvid:N}";
    }

    private static string FormatPublicKeyToken(PEFile peFile)
    {
        var identity = peFile.Metadata.GetAssemblyDefinition();
        var publicKey = peFile.Metadata.GetBlobBytes(identity.PublicKey);
        return publicKey.Length == 0 ? "neutral" : Convert.ToHexString(publicKey).ToLowerInvariant();
    }








    internal static CSharpProjectAnalysis? Analyze(
        string assemblyPath, IReadOnlySet<string> rootDocumentationIds, ICodeMapIdGenerator ids, CancellationToken cancellationToken)
    {
        PEFile peFile;
        try
        {
            peFile = new PEFile(assemblyPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }




        using var peFileLifetime = peFile;

        if (!peFile.IsAssembly)
            return null;

        DecompilerTypeSystem typeSystem;
        try
        {
            typeSystem = new DecompilerTypeSystem(peFile, new UniversalAssemblyResolver(assemblyPath, false, null));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            return null;
        }

        var projectName = BuildProjectName(peFile);
        var relativePath = GetSyntheticRelativePath(peFile);
        var byDocId = IndexEntitiesByDocumentationId(typeSystem);

        var nodes = new Dictionary<IEntity, CodeNode>();
        var edges = new List<CodeEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<IEntity>();
        var queue = new Queue<IEntity>();

        foreach (var docId in rootDocumentationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (byDocId.TryGetValue(docId, out var entity) && visited.Add(entity))
                queue.Enqueue(entity);
        }

        if (queue.Count == 0)
            return null;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = queue.Dequeue();
            var node = GetOrCreateNode(entity, projectName, relativePath, ids, nodes);

            if (entity is ITypeDefinition typeDefinition)
                AddTypeRelations(typeDefinition, node, ids, projectName, relativePath, nodes, edges, edgeKeys, visited, queue);
            else if (entity is IMethod { HasBody: true } method)
                AddMethodBodyRelations(method, node, peFile, typeSystem, projectName, relativePath, ids, nodes, edges, edgeKeys, visited, queue, cancellationToken);

            if (entity is IMember { IsOverride: true } overridingMember)
                AddOverrideRelation(overridingMember, node, projectName, relativePath, ids, nodes, edges, edgeKeys, visited, queue);
        }

        if (nodes.Count == 0)
            return null;

        var files = new[]
        {
            new CSharpSourceFile(assemblyPath, relativePath, ComputeAssemblyHashPlaceholder(assemblyPath))
        };
        var fileNode = new CodeNode
        {
            Id = ids.CreateFileId(projectName, relativePath),
            Kind = NodeKind.File,
            Name = Path.GetFileName(assemblyPath),
            QualifiedName = relativePath,
            FilePath = relativePath,
            Language = "csharp"
        };



        var declarationNodes = nodes.Values
            .DistinctBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        var allNodes = new List<CodeNode>(declarationNodes.Length + 1) { fileNode };
        allNodes.AddRange(declarationNodes);
        foreach (var node in declarationNodes)
            edges.Add(new CodeEdge { SourceId = fileNode.Id, TargetId = node.Id, Kind = EdgeKind.Defines, ResolutionKind = EdgeResolutionKind.Semantic, Confidence = 1.0 });

        return new CSharpProjectAnalysis(
            projectName,
            assemblyPath,
            files,
            new AnalysisResult { Nodes = allNodes, Edges = edges },
            PublicSurfaceFingerprint: null);
    }





    private static string ComputeAssemblyHashPlaceholder(string assemblyPath) =>
        $"external-assembly:{Path.GetFileName(assemblyPath)}";

    private static string GetSyntheticRelativePath(PEFile peFile) =>
        $"external/{Path.GetFileNameWithoutExtension(peFile.FileName)}.dll";









    private static Dictionary<string, IEntity> IndexEntitiesByDocumentationId(DecompilerTypeSystem typeSystem)
    {
        var byDocId = new Dictionary<string, IEntity>(StringComparer.Ordinal);
        void IndexType(ITypeDefinition type)
        {
            if (!IsCompilerGenerated(type))
            {
                var typeId = IdStringProvider.GetIdString(type);
                if (!string.IsNullOrEmpty(typeId))
                    byDocId.TryAdd(typeId, type);
            }
            foreach (var member in type.Members)
            {
                if (IsCompilerGenerated(member))
                    continue;
                var memberId = IdStringProvider.GetIdString(member);
                if (!string.IsNullOrEmpty(memberId))
                    byDocId.TryAdd(memberId, member);
            }
            foreach (var nested in type.NestedTypes)
                IndexType(nested);
        }
        foreach (var type in typeSystem.MainModule.TypeDefinitions)
            IndexType(type);
        return byDocId;
    }








    private static bool IsCompilerGenerated(IEntity entity) =>
        entity.Name.Contains('<', StringComparison.Ordinal)
        || (entity is IMethod { IsConstructor: true, Parameters.Count: 0 } ctor && ctor.MetadataToken.IsNil);









    private static CodeNode GetOrCreateNode(IEntity entity, string projectName, string relativePath, ICodeMapIdGenerator ids, Dictionary<IEntity, CodeNode> nodes)
    {
        if (nodes.TryGetValue(entity, out var existing))
            return existing;
        var documentationId = IdStringProvider.GetIdString(entity);
        var node = new CodeNode
        {
            Id = ids.CreateGlobalSymbolId(documentationId),
            Kind = GetNodeKind(entity),
            Name = entity.Name,
            QualifiedName = entity.ReflectionName,
            FilePath = relativePath,
            SourceLocation = null,
            Language = "csharp",
            Signature = entity is IMethod method ? FormatSignature(method) : null,
            Visibility = FormatVisibility(entity.Accessibility)
        };
        nodes.Add(entity, node);
        return node;
    }

    private static NodeKind GetNodeKind(IEntity entity) => entity switch
    {
        IMethod { IsConstructor: true } => NodeKind.Constructor,
        IMethod => NodeKind.Method,
        IProperty => NodeKind.Property,
        IField => NodeKind.Field,
        IEvent => NodeKind.Event,
        ITypeDefinition { Kind: TypeKind.Interface } => NodeKind.Interface,
        ITypeDefinition { Kind: TypeKind.Struct } => NodeKind.Struct,
        ITypeDefinition { Kind: TypeKind.Enum } => NodeKind.Enum,
        ITypeDefinition { Kind: TypeKind.Delegate } => NodeKind.Delegate,
        ITypeDefinition { IsRecord: true } => NodeKind.Record,
        ITypeDefinition => NodeKind.Class,
        _ => NodeKind.Function
    };

    private static string? FormatVisibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.None => null,
        var value => value.ToString().ToLowerInvariant()
    };

    private static string FormatSignature(IMethod method) =>
        (method.IsConstructor ? ".ctor" : method.Name)
        + (method.TypeParameters.Count == 0 ? string.Empty : "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">")
        + "(" + string.Join(", ", method.Parameters.Select(p => p.Type.ReflectionName)) + ")";








    private static void AddTypeRelations(
        ITypeDefinition type, CodeNode typeNode, ICodeMapIdGenerator ids, string projectName, string relativePath,
        Dictionary<IEntity, CodeNode> nodes, List<CodeEdge> edges, HashSet<string> edgeKeys,
        HashSet<IEntity> visited, Queue<IEntity> queue)
    {
        if (type.DirectBaseTypes is null)
            return;
        foreach (var baseType in type.DirectBaseTypes)
        {
            if (baseType.GetDefinition() is not { } baseDefinition || ReferenceEquals(baseDefinition, type))
                continue;
            if (!IsSameAssembly(baseDefinition, type))
                continue;
            if (visited.Add(baseDefinition))
                queue.Enqueue(baseDefinition);
            var baseNode = GetOrCreateNode(baseDefinition, projectName, relativePath, ids, nodes);
            var kind = baseDefinition.Kind == TypeKind.Interface ? EdgeKind.Implements : EdgeKind.Inherits;
            AddEdge(typeNode.Id, baseNode.Id, kind, edges, edgeKeys);
            if (kind == EdgeKind.Implements)
                AddEdge(baseNode.Id, typeNode.Id, EdgeKind.ImplementedBy, edges, edgeKeys);
        }
    }

    private static bool IsSameAssembly(IEntity a, IEntity b) =>
        ReferenceEquals(a.ParentModule, b.ParentModule);






    private static void AddOverrideRelation(
        IMember member, CodeNode node, string projectName, string relativePath, ICodeMapIdGenerator ids,
        Dictionary<IEntity, CodeNode> nodes, List<CodeEdge> edges, HashSet<string> edgeKeys,
        HashSet<IEntity> visited, Queue<IEntity> queue)
    {
        var baseMember = InheritanceHelper.GetBaseMember(member);
        if (baseMember is null || !IsSameAssembly(baseMember, member))
            return;
        if (visited.Add(baseMember))
            queue.Enqueue(baseMember);
        var baseNode = GetOrCreateNode(baseMember, projectName, relativePath, ids, nodes);
        AddEdge(node.Id, baseNode.Id, EdgeKind.Overrides, edges, edgeKeys);
    }












    private static void AddMethodBodyRelations(
        IMethod method, CodeNode sourceNode, PEFile peFile, DecompilerTypeSystem typeSystem, string projectName, string relativePath,
        ICodeMapIdGenerator ids, Dictionary<IEntity, CodeNode> nodes, List<CodeEdge> edges, HashSet<string> edgeKeys,
        HashSet<IEntity> visited, Queue<IEntity> queue, CancellationToken cancellationToken)
    {
        if (method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
            return;
        var handle = (MethodDefinitionHandle)method.MetadataToken;
        var methodDefinition = peFile.Metadata.GetMethodDefinition(handle);
        if (methodDefinition.RelativeVirtualAddress == 0)
            return;

        MethodBodyBlock methodBody;
        ILFunction ilFunction;
        try
        {
            methodBody = peFile.GetMethodBody(methodDefinition.RelativeVirtualAddress);
            var reader = new ILReader(typeSystem.MainModule);
            ilFunction = reader.ReadIL(handle, methodBody, default, ILFunctionKind.TopLevelFunction, cancellationToken);
        }
        catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException or InvalidOperationException)
        {
            return;
        }

        foreach (var instruction in ilFunction.Descendants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (instruction is not CallInstruction call)
                continue;
            var targetEntity = (IEntity)call.Method;
            if (!IsSameAssembly(targetEntity, method))
                continue;
            var kind = instruction is NewObj ? EdgeKind.Constructs : EdgeKind.Calls;
            var targetNode = GetOrCreateNode(targetEntity, projectName, relativePath, ids, nodes);
            if (visited.Add(targetEntity))
                queue.Enqueue(targetEntity);
            AddEdge(sourceNode.Id, targetNode.Id, kind, edges, edgeKeys);
        }
    }

    private static void AddEdge(string sourceId, string targetId, EdgeKind kind, List<CodeEdge> edges, HashSet<string> edgeKeys)
    {
        var key = $"{sourceId}{targetId}{kind}";
        if (sourceId == targetId || !edgeKeys.Add(key))
            return;
        edges.Add(new CodeEdge
        {
            SourceId = sourceId,
            TargetId = targetId,
            Kind = kind,
            ResolutionKind = EdgeResolutionKind.Semantic,
            Confidence = 1.0
        });
    }
}
