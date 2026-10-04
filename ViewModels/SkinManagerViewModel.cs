using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services.Skins;

namespace NexLauncher.ViewModels;

public sealed partial class SkinManagerViewModel : ObservableObject, IDisposable
{
    private readonly ISkinService _service;
    private readonly SkinValidator _validator;
    private readonly Func<Func<CancellationToken, Task>, Task> _run;
    private readonly Func<bool> _canEdit;
    private CancellationTokenSource? _request;
    private LauncherAccount? _account;
    private SkinImage? _current, _draft;
    private SkinModel _currentModel;
    private int _generation;
    private bool _disposed;
    public static SkinModelChoice[] Models { get; } = [new(SkinModel.Classic, "Classic / Steve · руки 4 px"), new(SkinModel.Slim, "Slim / Alex · руки 3 px")];
    [ObservableProperty] private SkinModelChoice _selectedModel = Models[0];
    [ObservableProperty] private SkinImage? _previewImage;
    [ObservableProperty] private string _status = "Открой скин выбранного аккаунта.";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isBusy;
    public Func<Task<string?>>? PickSkinAsync { get; set; }
    public string Username => _account?.Username ?? "Выбери аккаунт";
    public string AccountType => _account?.TypeLabel ?? "";
    public bool IsLocal => _account?.Type == NexLauncher.Models.AccountType.Local;
    public bool HasAccount => _account is not null;
    public bool IsEditable => !_disposed && !IsBusy && _canEdit();
    public bool HasChanges => _draft is not null || _current is not null && SelectedModel.Model != _currentModel;
    public bool IsMannequin => PreviewImage is null;
    public SkinModel PreviewModel => SelectedModel.Model;
    public string ApplyLabel => IsLocal ? "Сохранить Local Skin" : "Применить к Minecraft-аккаунту";
    public string ResetLabel => IsLocal ? "Удалить Local Skin" : "Вернуть стандартный скин";
    public string Scope => !HasAccount ? "Выбери аккаунт, чтобы управлять его скином." : IsLocal ? OfflineSkinService.Limitation : "Изменение применяется к Minecraft-аккаунту. Уже запущенная игра и другие клиенты могут обновить скин после переподключения.";
    public string PreviewHint => IsMannequin ? "Нейтральный манекен; это не точный стандартный скин Minecraft." : HasChanges ? "Предпросмотр · изменения ещё не применены." : IsLocal ? "Сохранённый Local Skin · только в NexLauncher." : "Текущий скин Minecraft-аккаунта.";
    public IAsyncRelayCommand OpenCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand UploadCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand ResetCommand { get; }
    public SkinManagerViewModel(ISkinService service, SkinValidator validator, Func<Func<CancellationToken, Task>, Task> run, Func<bool> canEdit)
    {
        _service = service; _validator = validator; _run = run; _canEdit = canEdit;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsEditable && HasAccount);
        OpenCommand = new AsyncRelayCommand(async () => { IsOpen = true; await RefreshAsync(); }, () => IsEditable && HasAccount);
        CloseCommand = new RelayCommand(() => { IsOpen = false; _request?.Cancel(); });
        UploadCommand = new AsyncRelayCommand(UploadAsync, () => IsEditable && HasAccount && PickSkinAsync is not null);
        ApplyCommand = new AsyncRelayCommand(() => RunAsync(async (account, token) => await _service.ApplyAsync(account, PreviewImage!, SelectedModel.Model, token),
            "Скин применён.", "Local Skin сохранён только в NexLauncher."), () => IsEditable && HasAccount && PreviewImage is not null && HasChanges);
        ResetCommand = new AsyncRelayCommand(() => RunAsync((account, token) => _service.ResetAsync(account, token),
            "Стандартный скин восстановлен. Minecraft выбирает его по UUID.", "Local Skin удалён. Скин в игре не изменялся."), () => IsEditable && HasAccount);
    }
    public void SetAccount(LauncherAccount? account)
    {
        _request?.Cancel(); _generation++; _account = account;
        _current = _draft = null; PreviewImage = null; _currentModel = SkinModel.Classic; SelectedModel = Models[0];
        Status = account is null ? "Выбери аккаунт." : "Нажми «Обновить», чтобы получить скин выбранного аккаунта.";
        foreach (var name in new[] { nameof(Username), nameof(AccountType), nameof(IsLocal), nameof(HasAccount), nameof(ApplyLabel), nameof(ResetLabel), nameof(Scope) }) OnPropertyChanged(name);
        RefreshCommands();
    }

    /// <summary>Import a source skin as a draft. Only the existing ApplyCommand changes the account.</summary>
    public async Task<bool> PrepareDraftAsync(SkinImage image, SkinModel model)
    {
        if (_account is null || !IsEditable) return false;
        var prepared = false; var generation = _generation;
        await _run(async token =>
        {
            if (_disposed || generation != _generation || _account is null) return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            _request = request; IsBusy = true;
            try
            {
                var clean = await Task.Run(() => _validator.Validate(image.Png, request.Token), request.Token);
                clean = clean with { Legacy = clean.Legacy || image.Legacy };
                SkinValidator.ValidateModel(clean, model);
                if (_disposed || generation != _generation || request.IsCancellationRequested) return;
                _draft = clean; PreviewImage = clean; SelectedModel = Models[(int)model]; IsOpen = true;
                Status = "Скин из библиотеки подготовлен. Проверь аккаунт и модель, затем нажми «" + ApplyLabel + "».";
                prepared = true;
            }
            finally { if (ReferenceEquals(_request, request)) { _request = null; IsBusy = false; } RefreshCommands(); }
        });
        return prepared;
    }
    private Task RefreshAsync() => RunAsync((account, token) => _service.GetAsync(account, token), "Скин аккаунта обновлён.", "Local Skin загружен с этого устройства.");
    private Task RunAsync(Func<LauncherAccount, CancellationToken, Task<AccountSkin>> action, string microsoftMessage, string localMessage) => _run(async outerToken =>
    {
        if (_account is not { } account || _disposed) return;
        var generation = _generation;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(outerToken); _request = source; IsBusy = true; Status = "Скины…";
        try
        {
            var result = await action(account, source.Token);
            if (_disposed || generation != _generation || source.IsCancellationRequested) return;
            _current = result.Image; _draft = null; _currentModel = result.Model;
            PreviewImage = result.Image; SelectedModel = Models[(int)result.Model];
            Status = result.Message.Length > 0 ? result.Message : account.Type == NexLauncher.Models.AccountType.Local ? localMessage : microsoftMessage;
        }
        catch (OperationCanceledException) { if (generation == _generation) Status = "Операция со скином отменена."; throw; }
        catch (Exception ex) { var error = FriendlyError(ex); if (generation == _generation) Status = error.Message; throw error; }
        finally { if (ReferenceEquals(_request, source)) { _request = null; IsBusy = false; } RefreshCommands(); }
    });
    private Task UploadAsync() => _run(async outerToken =>
    {
        if (PickSkinAsync is null || _account is null || _disposed) return;
        var generation = _generation; using var source = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        _request = source; IsBusy = true;
        try
        {
            var path = await PickSkinAsync(); if (path is null) return;
            var image = await _validator.ImportAsync(path, source.Token);
            if (_disposed || generation != _generation || source.IsCancellationRequested) return;
            _draft = image; PreviewImage = image;
            if (image.Legacy) SelectedModel = Models[0];
            Status = "PNG проверен. Выбери модель и нажми «" + ApplyLabel + "»." +
                (image.Legacy ? " Скин 64×32 преобразован в Classic 64×64." : "") +
                (image.NormalizedTransparency ? " Основной слой сделан непрозрачным, как в Minecraft; внешний слой сохранён." : "");
        }
        catch (OperationCanceledException) { if (generation == _generation) Status = "Импорт отменён."; throw; }
        catch (Exception ex) { var error = FriendlyError(ex); if (generation == _generation) Status = error.Message; throw error; }
        finally { if (ReferenceEquals(_request, source)) { _request = null; IsBusy = false; } RefreshCommands(); }
    });
    partial void OnSelectedModelChanged(SkinModelChoice value) { OnPropertyChanged(nameof(PreviewModel)); RefreshCommands(); }
    private static Exception FriendlyError(Exception error) => error switch
    {
        SkinException => error,
        InvalidDataException => new SkinException("Сохранённые данные скина повреждены. Оригинальные файлы оставлены для восстановления."),
        FileNotFoundException or DirectoryNotFoundException => new SkinException("Файл скина не найден. Выбери PNG заново или проверь сохранённые данные."),
        UnauthorizedAccessException => new SkinException("Нет доступа к файлу или папке скинов. Проверь разрешения Windows."),
        IOException => new SkinException("Не удалось прочитать или сохранить скин. Проверь доступ к диску и свободное место."),
        _ => error
    };
    partial void OnPreviewImageChanged(SkinImage? value) { OnPropertyChanged(nameof(IsMannequin)); RefreshCommands(); }
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    public void RefreshCommands()
    {
        OpenCommand?.NotifyCanExecuteChanged(); RefreshCommand?.NotifyCanExecuteChanged(); UploadCommand?.NotifyCanExecuteChanged();
        ApplyCommand?.NotifyCanExecuteChanged(); ResetCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEditable)); OnPropertyChanged(nameof(HasChanges)); OnPropertyChanged(nameof(PreviewHint));
    }
    public void Dispose() { _disposed = true; _request?.Cancel(); _current = _draft = null; PreviewImage = null; }
}
