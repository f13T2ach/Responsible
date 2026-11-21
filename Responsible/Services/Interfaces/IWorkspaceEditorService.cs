namespace Responsible.Services;

public interface IWorkspaceEditorService
{
    /// <summary>打开新建工作区对话框；取消时返回 null。</summary>
    Task<WorkspaceDefinition?> CreateAsync();
}
