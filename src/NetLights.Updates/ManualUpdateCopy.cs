namespace NetLights.Updates;

public static class ManualUpdateCopy
{
    public const string Checking = "Проверка…";
    public const string AlreadyRunning = "Проверка уже идёт.";

    public static string For(SilentUpdateOutcome outcome)
    {
        switch (outcome)
        {
            case SilentUpdateOutcome.NoUpdate:
                return "Уже установлена последняя версия.";
            case SilentUpdateOutcome.Applied:
                return "Обновление скачано, сейчас установится.";
            case SilentUpdateOutcome.Failed:
                return "Не удалось проверить или скачать.";
            case SilentUpdateOutcome.Offline:
                return "Не удалось связаться с GitHub.";
            case SilentUpdateOutcome.Skipped:
                return "Обновления доступны только установленной копии.";
            case SilentUpdateOutcome.Busy:
                return "Сейчас нельзя обновить. Попробуйте через минуту.";
            default:
                SilentUpdateOutcome unreachable = outcome;
                throw new InvalidOperationException($"Unhandled silent update outcome {unreachable}");
        }
    }
}
