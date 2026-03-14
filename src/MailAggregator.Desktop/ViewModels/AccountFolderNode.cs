using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MailAggregator.Core.Models;

namespace MailAggregator.Desktop.ViewModels;

public partial class AccountFolderNode : ObservableObject
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private int? _unreadCount;

    public Account? Account { get; set; }
    public MailFolder? Folder { get; set; }
    public bool IsAccount { get; set; }
    public ObservableCollection<AccountFolderNode> Children { get; } = [];
}
