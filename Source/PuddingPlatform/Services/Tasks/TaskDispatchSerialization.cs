using System.Text.Json;
using PuddingCode.Tasks;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-05: TaskInstructionEnvelope ↔ JSON 序列化（envelope_payload 列）。
/// <para>
/// 与 MessageFabricStore 同风格使用 <c>JsonSerializerDefaults.Web</c>；序列化与反序列化
/// 使用同一 options 保证 round-trip 稳定。
/// </para>
/// </summary>
public static class TaskDispatchSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(TaskInstructionEnvelope envelope)
        => JsonSerializer.Serialize(envelope, Options);

    public static TaskInstructionEnvelope Deserialize(string json)
        => JsonSerializer.Deserialize<TaskInstructionEnvelope>(json, Options)
           ?? throw new JsonException("TaskInstructionEnvelope payload deserialized to null.");
}
