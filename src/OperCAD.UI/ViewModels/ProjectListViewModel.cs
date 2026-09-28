using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OperCAD.Application;

namespace OperCAD.UI.ViewModels;

/// <summary>
/// ViewModel para o painel de projetos no painel esquerdo.
/// Gerencia a lista de projetos, criação, abertura, exclusão e renomeação.
/// </summary>
public sealed partial class ProjectListViewModel : ObservableObject
{
    private readonly ProjectRegistryService _registryService = new();
    private readonly IProjectFileService _fileService = new ProjectFileService();
    private Func<bool>? _confirmUnsavedChanges;
    private Action<Guid, string>? _openProjectAction;
    private Action? _clearWorkspaceAction;

    public ProjectListViewModel()
    {
        PortablePath.EnsureDirectories();
    }

    /// <summary>Lista de projetos exibida na árvore.</summary>
    public ObservableRangeCollection<ProjectListItemViewModel> Projects { get; } = new();

    [ObservableProperty]
    private ProjectListItemViewModel? _selectedProject;

    /// <summary>
    /// Configura os callbacks que ligam este ViewModel ao MainWindowViewModel.
    /// Chamado após a criação do MainWindowViewModel.
    /// </summary>
    public void Initialize(
        Func<bool> confirmUnsavedChanges,
        Action<Guid, string> openProjectAction,
        Action clearWorkspaceAction)
    {
        _confirmUnsavedChanges = confirmUnsavedChanges;
        _openProjectAction = openProjectAction;
        _clearWorkspaceAction = clearWorkspaceAction;
    }

    /// <summary>Carrega a lista de projetos do registro.</summary>
    public void LoadProjects()
    {
        var entries = _registryService.Load();
        var items = entries
            .OrderByDescending(e => e.LastModifiedAt)
            .Select(e => new ProjectListItemViewModel(this, e.Id, e.Name, e.LastModifiedAt))
            .ToList();
        Projects.ReplaceAll(items);
    }

    /// <summary>Cria um novo projeto. Retorna true se criado com sucesso, false se nome duplicado.</summary>
    public bool CreateProject(string name)
    {
        var entry = _registryService.Add(name);
        if (entry is null)
        {
            System.Windows.MessageBox.Show(
                $"Já existe um projeto com o nome \"{name}\".\nEscolha outro nome.",
                "OperCAD — Nome duplicado",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        // Cria um projeto vazio e salva no disco
        var project = new Core.Documents.Project { Name = name };
        var page = project.AddPage("Folha 01");
        var filePath = _registryService.GetFilePath(entry);
        _fileService.SaveAsync(project, filePath).GetAwaiter().GetResult();

        // Atualiza a lista
        LoadProjects();

        // Abre o projeto recém-criado
        OpenProject(entry.Id);
        return true;
    }

    /// <summary>Abre um projeto pelo seu Id.</summary>
    public void OpenProject(Guid id)
    {
        if (_confirmUnsavedChanges is null || _openProjectAction is null)
            return;

        // Pede confirmação se houver alterações não salvas
        if (MainWindowViewModel.Instance?.IsModified == true)
        {
            var shouldSave = _confirmUnsavedChanges();
            if (shouldSave)
            {
                MainWindowViewModel.Instance.SaveProjectCommand.Execute(null);
            }
        }

        var entry = _registryService.Load().FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return;

        var filePath = _registryService.GetFilePath(entry);
        _openProjectAction(id, filePath);

        // Atualiza LastModified
        _registryService.UpdateLastModified(id);
        LoadProjects();
    }

    /// <summary>Exclui um projeto com confirmação.</summary>
    public void DeleteProject(Guid id)
    {
        var entry = _registryService.Load().FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return;

        var result = System.Windows.MessageBox.Show(
            $"Excluir o projeto \"{entry.Name}\"?\n\nO arquivo será enviado para a Lixeira.",
            "OperCAD — Excluir projeto",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes)
            return;

        var wasOpen = MainWindowViewModel.Instance?.CurrentProjectId == id;

        _registryService.Remove(id);
        LoadProjects();

        if (wasOpen)
        {
            // Se era o projeto aberto, limpa a área de trabalho
            _clearWorkspaceAction?.Invoke();
        }
    }

    /// <summary>Renomeia um projeto. Retorna true se renomeado com sucesso, false se nome duplicado.</summary>
    public bool RenameProject(Guid id, string newName)
    {
        var success = _registryService.Rename(id, newName);
        if (!success)
        {
            System.Windows.MessageBox.Show(
                $"Já existe um projeto com o nome \"{newName}\".\nEscolha outro nome.",
                "OperCAD — Nome duplicado",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        var entry = _registryService.Load().FirstOrDefault(e => e.Id == id);

        // Se era o projeto aberto, atualiza o nome na janela
        if (entry is not null && MainWindowViewModel.Instance?.CurrentProjectId == id)
        {
            MainWindowViewModel.Instance.Project.Name = newName;
            MainWindowViewModel.Instance.WindowTitle = $"OperCAD — {newName}";
        }

        LoadProjects();
        return true;
    }

    /// <summary>Retorna o entry do registro de um projeto pelo Id.</summary>
    public ProjectRegistryEntry? GetEntry(Guid id)
        => _registryService.Load().FirstOrDefault(e => e.Id == id);
}
