# Pylo

Pylo is a mostly harmless "virus" made as a fun request. All Pylo does is rename every file on the desktop to `pylo`.

You can configure where logs are sent by replacing the value of:

```csharp
const string BuiltInWebhookUrl = "Webhook URL";
```

with your desired Discord webhook URL.

### Features

* **Undo**: Run Pylo with `--undo` to attempt restoring original filenames. (May cause issues if you manually rename, add, or remove files after running Pylo.)
* **Dry Run**: Run Pylo with `--dry-run` to preview changes. This will print actions to the console and send a dry-run log to the webhook without modifying any files.

> ## ⚠️ Disclaimer
> Pylo is not to be used with intent to harm others.
> Pylo is EXPLICITLY intended only for use with the knowledge and consent of the target user.