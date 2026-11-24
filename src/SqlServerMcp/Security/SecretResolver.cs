using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace SqlServerMcp.Security;

/// <summary>Secreto resuelto. No sobrescribe ToString para evitar fugas accidentales en logs.</summary>
public sealed class SecretValue
{
    private readonly char[] _value;

    public SecretValue(string userName, char[] value)
    {
        UserName = userName;
        _value = value;
    }

    /// <summary>Usuario asociado (Administrador de credenciales); puede estar vacío.</summary>
    public string UserName { get; }

    /// <summary>Copia del secreto como SecureString de solo lectura (para SqlCredential).</summary>
    public SecureString ToSecureString()
    {
        var s = new SecureString();
        foreach (var c in _value)
        {
            s.AppendChar(c);
        }

        s.MakeReadOnly();
        return s;
    }

    /// <summary>Secreto en texto (solo para APIs que lo exigen, por ejemplo el secreto de una entidad de servicio o la URL de un webhook).</summary>
    public string Reveal() => new(_value);

    public override string ToString() => "***";
}

public sealed class SecretResolutionException(string message) : Exception(message);

/// <summary>
/// Resuelve referencias a secretos (RS-SEC-02): "wincred:&lt;destino&gt;" (Administrador de credenciales de Windows),
/// "env:&lt;VARIABLE&gt;" (variable inyectada por el cliente MCP) o "dpapi:&lt;ruta&gt;" (archivo protegido con DPAPI del usuario).
/// Los mensajes de error nunca incluyen el valor del secreto.
/// </summary>
public interface ISecretResolver
{
    SecretValue Resolve(string reference);
}

public sealed partial class SecretResolver : ISecretResolver
{
    public SecretValue Resolve(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var idx = reference.IndexOf(':', StringComparison.Ordinal);
        if (idx <= 0)
        {
            throw new SecretResolutionException("Referencia de secreto inválida.");
        }

        var scheme = reference[..idx].ToLowerInvariant();
        var target = reference[(idx + 1)..];
        return scheme switch
        {
            "env" => FromEnvironment(target),
            "wincred" => OperatingSystem.IsWindows() ? FromCredentialManager(target) : throw new SecretResolutionException("wincred: solo está disponible en Windows."),
            "dpapi" => OperatingSystem.IsWindows() ? FromDpapiFile(target) : throw new SecretResolutionException("dpapi: solo está disponible en Windows."),
            _ => throw new SecretResolutionException($"Esquema de secreto '{scheme}' no admitido."),
        };
    }

    private static SecretValue FromEnvironment(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value))
        {
            throw new SecretResolutionException($"La variable de entorno '{variable}' no está definida.");
        }

        return new SecretValue(string.Empty, value.ToCharArray());
    }

    [SupportedOSPlatform("windows")]
    private static SecretValue FromDpapiFile(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(full))
        {
            throw new SecretResolutionException("No se encontró el archivo DPAPI indicado en la referencia.");
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(full);
            var bytes = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            try
            {
                return new SecretValue(string.Empty, Encoding.UTF8.GetChars(bytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (CryptographicException)
        {
            throw new SecretResolutionException("No fue posible descifrar el archivo DPAPI (¿otro usuario o equipo?).");
        }
    }

    /// <summary>Entropía adicional fija de la aplicación para los archivos DPAPI.</summary>
    internal static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("SqlServerMcp.v1");

    /// <summary>Protege un secreto con DPAPI (usuario actual) y lo guarda en un archivo (comando --protect-secret).</summary>
    [SupportedOSPlatform("windows")]
    public static void ProtectToFile(string secret, string path)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecretValue FromCredentialManager(string target)
    {
        if (!NativeMethods.CredRead(target, NativeMethods.CredTypeGeneric, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new SecretResolutionException(error == 1168
                ? $"No existe la credencial '{target}' en el Administrador de credenciales de Windows."
                : $"No fue posible leer la credencial '{target}' (error {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(credentialPtr);
            var user = credential.UserName == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;
            var size = (int)credential.CredentialBlobSize;
            if (size == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                throw new SecretResolutionException($"La credencial '{target}' no tiene contraseña.");
            }

            var bytes = new byte[size];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, size);
            try
            {
                // cmdkey y la UI de Windows guardan la contraseña como UTF-16LE.
                return new SecretValue(user, Encoding.Unicode.GetChars(bytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            NativeMethods.CredFree(credentialPtr);
        }
    }

    [SupportedOSPlatform("windows")]
    private static partial class NativeMethods
    {
        public const int CredTypeGeneric = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct Credential
        {
            public int Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public int LastWrittenLow;
            public int LastWrittenHigh;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
        public static partial void CredFree(IntPtr buffer);
    }
}
