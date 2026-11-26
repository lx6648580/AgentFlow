using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;

namespace AgentFlow.Editor.POC.Pages;

public partial class ModelManager : IDisposable
{
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Parameter, EditorRequired] public Home Board { get; set; } = default!;
    [Parameter] public string Culture { get; set; } = "en";
    [Parameter, EditorRequired] public IReadOnlyList<ModelProfile> Profiles { get; set; } = [];
    [Parameter] public bool Available { get; set; }
    [Parameter] public EventCallback Reload { get; set; }
    [Parameter] public EventCallback<ModelProfile> Saved { get; set; }

    private ElementReference dialog;
    private HttpClient? http;
    private ModelProfile? savedProfile;
    private string profileId = "", baseUrl = "", modelId = "", secretRef = "";
    private int timeout = 60, maxTokens = 128;
    private double temperature = 0.2;
    private bool streaming = true;
    private bool initialized;
    private bool busy;
    private bool showMcp;
    private string? feedback;

    private string T(string key) => UiText.Get(key, Culture);
    private bool IsMock => savedProfile?.Id == "mock";
    private bool CanProbe => savedProfile is not null && savedProfile == Draft();

    protected override void OnInitialized()
    {
        if (HostApi.BaseAddress(Configuration["NodeCatalogUrl"]) is { } host)
            http = new() { BaseAddress = host, Timeout = Timeout.InfiniteTimeSpan };
    }

    protected override void OnParametersSet()
    {
        if (!initialized)
        {
            initialized = true;
            SetProfile(Profiles.FirstOrDefault());
        }
        else if (savedProfile is { } selected && selected == Draft() &&
                 Profiles.FirstOrDefault(profile => profile.Id == selected.Id) is { } latest && latest != selected)
        {
            SetProfile(latest);
        }
    }

    public async Task OpenAsync()
    {
        await Js.InvokeVoidAsync("agentFlow.showModal", dialog);
        if (!busy)
            await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        busy = true;
        StateHasChanged();
        try { await Reload.InvokeAsync(); }
        finally
        {
            busy = false;
            StateHasChanged();
        }
    }

    private void NewProfile() => SetProfile(null);

    private void SetProfile(ModelProfile? selected)
    {
        savedProfile = selected;
        var draft = selected ?? new("", "", "");
        (profileId, baseUrl, modelId, secretRef) = (draft.Id, draft.BaseUrl, draft.ModelId, draft.SecretRef ?? "");
        (timeout, temperature, maxTokens, streaming) = (draft.TimeoutSeconds, draft.Temperature, draft.MaxTokens, draft.Streaming);
        feedback = null;
    }

    private ModelProfile Draft() => new(profileId.Trim(), baseUrl.Trim(), modelId.Trim(),
        string.IsNullOrWhiteSpace(secretRef) ? null : secretRef.Trim(), timeout, temperature, maxTokens, streaming);

    private async Task SaveAsync()
    {
        if (http is null || busy || !Available)
            return;
        var draft = Draft();
        var url = savedProfile is null ? "api/models?createOnly=true" : "api/models";
        busy = true;
        feedback = null;
        try
        {
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await http.PostAsJsonAsync(url, draft, request.Token);
            if (response.IsSuccessStatusCode)
            {
                SetProfile(draft);
                await Saved.InvokeAsync(draft);
                feedback = "ProfileSaved";
            }
            else
                (feedback, _) = await HostApi.ReadErrorAsync(response, request.Token, "ModelRequestFailed", "UnknownModelProfile");
        }
        catch (Exception error) when (HostApi.IsNetworkError(error)) { feedback = "ModelCatalogUnavailable"; }
        finally { busy = false; }
    }

    private async Task ProbeAsync()
    {
        if (http is null || busy || !Available || !CanProbe || savedProfile is not { } profile)
            return;
        busy = true;
        feedback = "Probing";
        try
        {
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(profile.TimeoutSeconds + 5));
            using var response = await http.PostAsync($"api/models/{profile.Id}/tools", null, request.Token);
            if (response.IsSuccessStatusCode)
            {
                var probe = await response.Content.ReadFromJsonAsync<ToolProbe>(cancellationToken: request.Token);
                feedback = probe?.Status switch
                {
                    "Supported" => "ToolsSupported",
                    "NotObserved" => "ToolsNotObserved",
                    _ => "ToolsUnavailable"
                };
            }
            else
                (feedback, _) = await HostApi.ReadErrorAsync(response, request.Token, "ModelRequestFailed", "UnknownModelProfile");
        }
        catch (Exception error) when (HostApi.IsNetworkError(error)) { feedback = "ModelCatalogUnavailable"; }
        finally { busy = false; }
    }

    public void Dispose() => http?.Dispose();
}
