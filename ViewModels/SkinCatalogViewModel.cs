using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Skins.Catalog;

namespace NexLauncher.ViewModels;

public sealed partial class SkinCatalogCardViewModel(SkinCatalogItem item) : ObservableObject, IDisposable
{
    public SkinCatalogItem Item { get; } = item;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private string _notice = "Загрузка preview…";
    public void Dispose() { Thumbnail?.Dispose(); Thumbnail = null; }
}

public sealed partial class SkinCatalogViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 24;
    private readonly ISkinCatalogSource _source;
    private readonly SkinLibrary _library;
    private readonly SkinCatalogPreviewCache _previews;
    private readonly SkinPngExport _export;
    private readonly Func<bool> _canEdit;
    private readonly Func<LauncherAccount, SkinCatalogTexture, Task> _use;
    private readonly Action<string> _log;
    private CancellationTokenSource? _search, _details, _operation;
    private SkinCatalogTexture? _texture;
    private bool _active, _loaded, _disposed;
    private int _offset, _total;
    public ObservableCollection<SkinCatalogCardViewModel> Results { get; } = new();
    public ObservableCollection<LauncherAccount> Accounts { get; } = new();
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoadingDetails;
    [ObservableProperty] private bool _showRemoveConfirmation;
    [ObservableProperty] private SkinCatalogItem? _selectedItem;
    [ObservableProperty] private SkinImage? _previewImage;
    [ObservableProperty] private SkinModelChoice? _selectedModel = SkinManagerViewModel.Models[0];
    [ObservableProperty] private LauncherAccount? _targetAccount;
    public Func<Task<string?>>? PickImportAsync { get; set; }
    public Func<string, Task<string?>>? PickExportAsync { get; set; }
    public SkinCatalogSource Source => _source.Source;
    public IReadOnlyList<SkinModelChoice> AvailableModels => SelectedItem?.Legacy == true ?
        [SkinManagerViewModel.Models[0]] : SkinManagerViewModel.Models;
    public bool IsDetails => SelectedItem is not null;
    public bool IsBrowsing => !IsDetails;
    public bool HasError => Error.Length != 0;
    public bool HasStatus => Status.Length != 0;
    public bool IsEmpty => !IsSearching && !HasError && Results.Count == 0;
    public bool IsEditable => !_disposed && !IsBusy && _canEdit();
    public bool CanUse => IsEditable && _texture is not null && TargetAccount is not null && !IsLoadingDetails &&
        SelectedModel is { } choice && (!_texture.Image.Legacy || choice.Model == SkinModel.Classic);
    public bool HasModelChanges => _texture is not null && SelectedModel is { } choice && _texture.Model != choice.Model;
    public SkinModel PreviewModel => SelectedModel?.Model ?? _texture?.Model ?? SkinModel.Classic;
    public string PreviewHint => PreviewImage is null ? "Preview пока недоступен; показан нейтральный манекен." : "Перетаскивай для поворота · колесо для масштаба";
    public string PageLabel => _total == 0 ? "0 скинов" : $"{_offset + 1}–{_offset + Results.Count} из {_total}";
    public string AccountScope => TargetAccount is null ? "Выбери аккаунт. Его активность для запуска не изменится." :
        TargetAccount.Type == AccountType.Local ? "Local Skin сохраняется только в NexLauncher; скин в игре не подменяется." :
        "Здесь готовится только черновик. Изменение Microsoft-аккаунта нужно подтвердить кнопкой применения в Skin Manager.";
    public double ScrollOffset { get; set; }
    public int SearchRevision { get; private set; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand<SkinCatalogCardViewModel> DownloadCardCommand { get; }
    public IAsyncRelayCommand SaveModelCommand { get; }
    public IAsyncRelayCommand UseCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand ConfirmRemoveCommand { get; }
    public IRelayCommand<SkinCatalogCardViewModel> DetailsCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand KeepCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand<string> OpenSourceCommand { get; }

    public SkinCatalogViewModel(ISkinCatalogSource source, SkinLibrary library, SkinCatalogPreviewCache previews,
        SkinPngExport export, Func<bool> canEdit, Func<LauncherAccount, SkinCatalogTexture, Task> use, Action<string> log)
    {
        _source = source; _library = library; _previews = previews; _export = export;
        _canEdit = canEdit; _use = use; _log = log;
        SearchCommand = new AsyncRelayCommand(() => SearchAsync(false), () => IsEditable && !IsSearching);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => IsEditable && PickImportAsync is not null);
        DetailsCommand = new RelayCommand<SkinCatalogCardViewModel>(x => { if (x is not null) _ = DetailsAsync(x.Item); }, _ => IsEditable);
        BackCommand = new RelayCommand(() => { _details?.Cancel(); SelectedItem = null; _texture = null; PreviewImage = null; Error = ""; ShowRemoveConfirmation = false; }, () => IsEditable);
        DownloadCommand = new AsyncRelayCommand(() => DownloadAsync(SelectedItem!), () => IsEditable && SelectedItem is not null && !IsLoadingDetails && PickExportAsync is not null);
        DownloadCardCommand = new AsyncRelayCommand<SkinCatalogCardViewModel>(x => x is null ? Task.CompletedTask : DownloadAsync(x.Item), _ => IsEditable && PickExportAsync is not null);
        SaveModelCommand = new AsyncRelayCommand(() => OperateAsync(async token =>
        {
            var chosen = SelectedModel!.Model;
            SelectedItem = await _library.SetModelAsync(SelectedItem!, chosen, token);
            _texture = _texture! with { Model = chosen }; SelectedModel = SkinManagerViewModel.Models[(int)chosen];
            await SearchAsync(false, preserveScroll: true);
            Status = "Модель сохранена в библиотеке; скин аккаунта не менялся.";
        }), () => IsEditable && SelectedItem?.SourceId == _library.Source.Id && HasModelChanges && !IsLoadingDetails &&
            (_texture?.Image.Legacy != true || SelectedModel?.Model == SkinModel.Classic));
        UseCommand = new AsyncRelayCommand(() => OperateAsync(async token =>
        {
            var account = TargetAccount!;
            var chosen = SelectedModel!.Model;
            var texture = await _source.ReadAsync(SelectedItem!, token);
            await _use(account, texture with { Model = chosen });
        }), () => CanUse);
        NextCommand = new AsyncRelayCommand(() => SearchAsync(false, _offset + PageSize), () => IsEditable && !IsSearching && _offset + PageSize < _total);
        PreviousCommand = new AsyncRelayCommand(() => SearchAsync(false, Math.Max(0, _offset - PageSize)), () => IsEditable && !IsSearching && _offset > 0);
        RemoveCommand = new RelayCommand(() => ShowRemoveConfirmation = true, () => IsEditable && SelectedItem?.SourceId == _library.Source.Id);
        KeepCommand = new RelayCommand(() => ShowRemoveConfirmation = false);
        ConfirmRemoveCommand = new AsyncRelayCommand(() => OperateAsync(async token =>
        {
            var message = await _library.RemoveAsync(SelectedItem!, token);
            SelectedItem = null; _texture = null; PreviewImage = null; ShowRemoveConfirmation = false;
            await SearchAsync(false, 0); Status = message;
        }), () => IsEditable && ShowRemoveConfirmation && SelectedItem?.SourceId == _library.Source.Id);
        CancelCommand = new RelayCommand(() => { _search?.Cancel(); _details?.Cancel(); _operation?.Cancel(); }, () => IsSearching || IsBusy || IsLoadingDetails);
        OpenSourceCommand = new RelayCommand<string>(OpenSource);
    }

    public void Open(IEnumerable<LauncherAccount> accounts, LauncherAccount? preferred)
    {
        _active = true;
        var previous = TargetAccount; Accounts.Clear(); foreach (var account in accounts) Accounts.Add(account);
        TargetAccount = Accounts.FirstOrDefault(x => previous is not null && x.Id == previous.Id && x.Type == previous.Type) ??
            Accounts.FirstOrDefault(x => preferred is not null && x.Id == preferred.Id && x.Type == preferred.Type) ?? Accounts.FirstOrDefault();
        if (!_loaded) _ = SearchAsync(false, preserveScroll: true);
        if (SelectedItem is { } selected && _texture is null) _ = DetailsAsync(selected);
        RefreshCommands();
    }
    public void Deactivate() { _active = false; _search?.Cancel(); _details?.Cancel(); _operation?.Cancel(); }
    private async Task SearchAsync(bool debounce, int? offset = null, bool preserveScroll = false)
    {
        if (!_active || _disposed) return;
        _search?.Cancel(); using var request = CancellationTokenSource.CreateLinkedTokenSource(_operation?.Token ?? default); _search = request;
        IsSearching = true; Error = "";
        try
        {
            if (debounce) await Task.Delay(350, request.Token);
            var requested = offset ?? _offset;
            var page = await _source.SearchAsync(Query, requested, PageSize, request.Token);
            if (requested > 0 && requested >= page.Total)
            { requested = Math.Max(0, (page.Total - 1) / PageSize * PageSize); page = await _source.SearchAsync(Query, requested, PageSize, request.Token); }
            request.Token.ThrowIfCancellationRequested();
            if (!_active || _disposed || !ReferenceEquals(_search, request)) return;
            ClearResults(); foreach (var item in page.Items) Results.Add(new(item));
            _offset = requested; _total = page.Total; _loaded = true;
            Status = page.Notice;
            if (!preserveScroll) ScrollOffset = 0;
            SearchRevision++; OnPropertyChanged(nameof(SearchRevision)); OnPropertyChanged(nameof(PageLabel));
            foreach (var batch in Results.ToArray().Chunk(4)) await Task.WhenAll(batch.Select(x => ThumbnailAsync(x, request.Token)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ReferenceEquals(_search, request) && !request.IsCancellationRequested) Fail(ex); }
        finally { if (ReferenceEquals(_search, request)) { _search = null; IsSearching = false; } }
    }
    private async Task ThumbnailAsync(SkinCatalogCardViewModel card, CancellationToken token)
    {
        Bitmap? bitmap = null;
        try
        {
            var texture = await _source.ReadAsync(card.Item, token);
            var bytes = await _previews.GetAsync(texture, token);
            bitmap = await Task.Run(() => { using var stream = new MemoryStream(bytes); return new Bitmap(stream); }, token);
            if (_disposed || token.IsCancellationRequested || !Results.Contains(card)) return;
            card.Thumbnail = bitmap; bitmap = null; card.Notice = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && Results.Contains(card)) { card.Notice = "Preview недоступен. Открой запись для диагностики."; _log(LauncherLog.Sanitize(ex.ToString())); } }
        finally { bitmap?.Dispose(); }
    }
    private async Task DetailsAsync(SkinCatalogItem item)
    {
        _details?.Cancel(); using var request = CancellationTokenSource.CreateLinkedTokenSource(_operation?.Token ?? default); _details = request;
        SelectedItem = item; PreviewImage = null; _texture = null; SelectedModel = SkinManagerViewModel.Models[(int)item.Model];
        IsLoadingDetails = true; Error = ""; ShowRemoveConfirmation = false;
        try
        {
            var texture = await _source.ReadAsync(item, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_disposed || !_active || !ReferenceEquals(_details, request)) return;
            _texture = texture; PreviewImage = texture.Image; SelectedModel = SkinManagerViewModel.Models[(int)texture.Model];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested && ReferenceEquals(_details, request)) Fail(ex); }
        finally { if (ReferenceEquals(_details, request)) { _details = null; IsLoadingDetails = false; } RefreshCommands(); }
    }
    private Task ImportAsync() => OperateAsync(async token =>
    {
        var path = await PickImportAsync!(); if (path is null) return;
        var item = await _library.ImportAsync(path, SkinModel.Classic, token);
        Query = ""; _offset = 0; await SearchAsync(false);
        token.ThrowIfCancellationRequested();
        await DetailsAsync(item);
        token.ThrowIfCancellationRequested();
        Status = "PNG импортирован. Для 64×64 выбери Classic или Slim; модель не угадывается по пикселям. Аккаунт ещё не изменён.";
    });
    private Task DownloadAsync(SkinCatalogItem item) => OperateAsync(async token =>
    {
        var texture = await _source.ReadAsync(item, token);
        var path = await PickExportAsync!("skin-" + item.Id + ".png");
        if (path is null) return;
        await _export.SaveAsync(texture.Image, path, token); Status = "Проверенный PNG сохранён.";
    });
    private async Task OperateAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) return;
        using var operation = new CancellationTokenSource(); _operation = operation;
        IsBusy = true; Error = ""; Status = "";
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!_disposed) Status = "Операция отменена."; }
        catch (Exception ex) { if (!_disposed) Fail(ex); }
        finally { if (ReferenceEquals(_operation, operation)) _operation = null; IsBusy = false; RefreshCommands(); }
    }
    private void Fail(Exception error)
    {
        Error = error is SkinException ? error.Message : error is FileNotFoundException or DirectoryNotFoundException ?
            "Файл скина не найден. Импортируй PNG заново." : error is UnauthorizedAccessException ?
            "Нет доступа к библиотеке или файлу. Проверь разрешения Windows." :
            "Не удалось прочитать или сохранить скин. Исходные файлы оставлены; проверь доступ к диску.";
        _log(LauncherLog.Sanitize(error.ToString()));
    }
    private void OpenSource(string? source)
    {
        var url = source switch { "namemc" => "https://namemc.com/", "skinmc" => "https://skinmc.net/", "mineskin" => "https://mineskin.org/", _ => null };
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Fail(ex); }
    }
    partial void OnQueryChanged(string value) { _offset = 0; if (_active && IsBrowsing && !IsBusy) _ = SearchAsync(true); }
    partial void OnSelectedItemChanged(SkinCatalogItem? value) { OnPropertyChanged(nameof(IsDetails)); OnPropertyChanged(nameof(IsBrowsing)); OnPropertyChanged(nameof(AvailableModels)); RefreshCommands(); }
    partial void OnSelectedModelChanged(SkinModelChoice? value) { OnPropertyChanged(nameof(PreviewModel)); RefreshCommands(); }
    partial void OnTargetAccountChanged(LauncherAccount? value) { OnPropertyChanged(nameof(AccountScope)); RefreshCommands(); }
    partial void OnIsSearchingChanged(bool value) { OnPropertyChanged(nameof(IsEmpty)); RefreshCommands(); }
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnIsLoadingDetailsChanged(bool value) => RefreshCommands();
    partial void OnShowRemoveConfirmationChanged(bool value) => RefreshCommands();
    partial void OnErrorChanged(string value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(IsEmpty)); }
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnPreviewImageChanged(SkinImage? value) => OnPropertyChanged(nameof(PreviewHint));
    public void RefreshCommands()
    {
        SearchCommand?.NotifyCanExecuteChanged(); ImportCommand?.NotifyCanExecuteChanged(); DetailsCommand?.NotifyCanExecuteChanged();
        DownloadCommand?.NotifyCanExecuteChanged(); DownloadCardCommand?.NotifyCanExecuteChanged(); BackCommand?.NotifyCanExecuteChanged();
        SaveModelCommand?.NotifyCanExecuteChanged(); UseCommand?.NotifyCanExecuteChanged(); NextCommand?.NotifyCanExecuteChanged(); PreviousCommand?.NotifyCanExecuteChanged();
        RemoveCommand?.NotifyCanExecuteChanged(); ConfirmRemoveCommand?.NotifyCanExecuteChanged(); CancelCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEditable)); OnPropertyChanged(nameof(CanUse)); OnPropertyChanged(nameof(HasModelChanges));
    }
    private void ClearResults() { foreach (var card in Results) card.Dispose(); Results.Clear(); OnPropertyChanged(nameof(IsEmpty)); }
    public void Dispose() { _disposed = true; Deactivate(); ClearResults(); PreviewImage = null; _texture = null; }
}
