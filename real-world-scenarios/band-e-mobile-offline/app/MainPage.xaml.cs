using BandEMobile.Chat;

namespace BandEMobile;

public partial class MainPage : ContentPage
{
    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<ChatMessage> _conversation =
    [
        new ChatMessage("system", "You are a Band E probe. Answer in one short sentence."),
        new ChatMessage("user", "Is the device online?"),
    ];

    public MainPage()
    {
        InitializeComponent();

        // The transport is constructed here rather than injected so the page can be created
        // by the default MAUI host without a DI registration; swapping in a remote transport
        // later means changing only this line.
        _chatClient = new FoundryLocalChatClient(Platform.AppContext);

        var availability = FoundryLocalProbe.Check(Platform.AppContext);
        TransportLabel.Text = $"transport: {_chatClient.Name}";
        AvailabilityLabel.Text = $"companion: {availability}";
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        SendButton.IsEnabled = false;
        ResultLabel.Text = "sending...";

        try
        {
            var result = await _chatClient.CompleteAsync(_conversation);

            if (result.Succeeded)
            {
                var completion = result.Completion!;
                TranscriptLabel.Text = string.Join(
                    Environment.NewLine,
                    _conversation.Select(m => $"{m.Role}: {m.Text}").Append($"assistant: {completion.Text}"));
                ResultLabel.Text = $"completed via {completion.TransportName}";
            }
            else
            {
                var failure = result.Failure!;

                // Distinguish "the service app is missing" from "the bridge itself is broken":
                // the probe exercises the JNI boundary even when IPC cannot start.
                var probe = await BridgeShim.ProbeStartAsync(Platform.AppContext!, "BandEMobile");
                // The classification is on screen on purpose: the evidence file records what
                // the device actually reported, not what the app wished for.
                ResultLabel.Text =
                    $"failed [{failure.Kind}] {(failure.IsRetryable ? "retryable" : "not retryable")}: {failure.Message}"
                    + Environment.NewLine + probe;
            }
        }
        catch (Exception ex)
        {
            ResultLabel.Text = $"unhandled {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }
}
