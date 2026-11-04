using System.Reflection;
using IATec.Shared.HttpClient.Dto;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;

/// <summary>
/// Builds <see cref="BaseResponseDto"/> instances for tests.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BaseResponseDto.Success"/> has a private setter and the mutators
/// (<c>SetSuccess</c>, <c>AddError</c>) are <c>internal</c> to the <c>IATec.Shared.HttpClient</c>
/// assembly, so a test in a different assembly cannot set them through the public API. To exercise
/// the sender's real success/failure branches we construct the stub response object via reflection.
/// </para>
/// <para>
/// This only shapes the fake transport result the sender receives; it does not touch the sender's
/// own logic, which is what the tests assert.
/// </para>
/// </remarks>
internal static class BaseResponseDtoFactory
{
    private static readonly MethodInfo SetSuccessMethod =
        typeof(BaseResponseDto).GetMethod(
            "SetSuccess",
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(bool)],
            modifiers: null)
        ?? throw new InvalidOperationException("BaseResponseDto.SetSuccess(bool) not found.");

    private static readonly MethodInfo AddErrorWithStatusMethod =
        typeof(BaseResponseDto).GetMethod(
            "AddError",
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(int?), typeof(string)],
            modifiers: null)
        ?? throw new InvalidOperationException("BaseResponseDto.AddError(int?, string) not found.");

    /// <summary>Builds a successful response (<c>Success == true</c>, no errors).</summary>
    public static BaseResponseDto Success()
    {
        var dto = new BaseResponseDto();
        SetSuccessMethod.Invoke(dto, [true]);
        return dto;
    }

    /// <summary>
    /// Builds an unsuccessful response (<c>Success == false</c>) carrying the supplied errors.
    /// </summary>
    public static BaseResponseDto Failure(int? statusCode, string message)
    {
        var dto = new BaseResponseDto();
        AddErrorWithStatusMethod.Invoke(dto, [statusCode, message]);
        return dto;
    }
}
