namespace Enfolderer.App.Utilities;

/// <summary>
/// The parts of a device code challenge a prompt needs. The identity library hands these over as
/// one prose message; kept apart here so the UI can make the code selectable and open the URL
/// itself instead of asking someone to retype either.
/// </summary>
/// <param name="UserCode">The code to enter in the browser.</param>
/// <param name="VerificationUri">Where to enter it, e.g. https://microsoft.com/devicelogin.</param>
/// <param name="Message">The library's own wording, kept as a fallback and for logs.</param>
public sealed record DeviceCodeDetails(string UserCode, string VerificationUri, string Message);
