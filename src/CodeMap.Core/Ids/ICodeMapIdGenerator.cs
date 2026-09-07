namespace CodeMap.Core.Ids;

public interface ICodeMapIdGenerator
{
    string CreateFileId(string projectName, string relativePath);

    string CreateSymbolId(string projectName, string qualifiedSymbolSignature);






    string CreateGlobalSymbolId(string documentationCommentId);
}
