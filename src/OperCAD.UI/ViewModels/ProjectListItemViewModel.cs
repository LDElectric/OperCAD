using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OperCAD.UI.ViewModels;

/// <summary>
/// ViewModel para um item na lista de projetos do painel esquerdo.
/// Suporta renomear via clique lento (estilo Windows Explorer).
/// </summary>
public sealed partial class ProjectListItemViewModel : ObservableObject
{
    private readonly ProjectListViewModel _owner;

    public ProjectListItemViewModel(ProjectListViewModel owner, Guid id, string name, DateTime lastModifiedAt)
    {
        _owner = owner;
        Id = id;
        Name = name;
        _originalName = name;
        LastModifiedAt = lastModifiedAt;
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private DateTime _lastModifiedAt;

    [ObservableProperty]
    private bool _isEditing;

    /// <summary>Estado de seleção do item (sincronizado com a TreeView via TwoWay).</summary>
    [ObservableProperty]
    private bool _isSelected;

    private string _originalName;

    /// <summary>Texto exibido (nome do projeto).</summary>
    public string DisplayText => Name;

    /// <summary>Data formatada para exibição (fonte menor).</summary>
    public string DateDisplay => LastModifiedAt.ToString("dd/MM/yyyy HH:mm");

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayText));
    }

    partial void OnLastModifiedAtChanged(DateTime value)
    {
        OnPropertyChanged(nameof(DateDisplay));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        // Sincroniza o item selecionado com o owner (usado pelo menu de contexto).
        if (value)
            _owner.SelectedProject = this;
    }

    /// <summary>Entra no modo de renomear (mostra TextBox).</summary>
    [RelayCommand]
    private void BeginRename()
    {
        _originalName = Name;
        IsEditing = true;
    }

    /// <summary>Confirma a renomeação.</summary>
    [RelayCommand]
    private void CommitRename()
    {
        var newName = Name?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            Name = _originalName;
            IsEditing = false;
            return;
        }

        if (newName != _originalName)
        {
            var success = _owner.RenameProject(Id, newName);
            if (!success)
            {
                // Nome duplicado — reverte para o nome original
                Name = _originalName;
                IsEditing = false;
                return;
            }
        }
        IsEditing = false;
    }

    /// <summary>Cancela a renomeação e reverte para o nome original.</summary>
    [RelayCommand]
    private void CancelRename()
    {
        Name = _originalName;
        IsEditing = false;
    }

    /// <summary>Abre este projeto (chamado pelo duplo-clique rápido).</summary>
    [RelayCommand]
    private void Open()
    {
        _owner.OpenProject(Id);
    }

    /// <summary>Exclui este projeto com confirmação.</summary>
    [RelayCommand]
    private void Delete()
    {
        _owner.DeleteProject(Id);
    }
}
