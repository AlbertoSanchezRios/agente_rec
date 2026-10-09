using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenRecorder.Core.Services;

/// <summary>
/// Protección anti-manipulación de procesos: denegación de PROCESS_TERMINATE al grupo
/// "Usuarios" (SID S-1-5-32-545) mediante una DACL propia.
///
/// Con esto, un usuario ESTÁNDAR no puede finalizar el grabador ni FFmpeg desde el
/// Administrador de tareas, taskkill ni ninguna API de terminación. SISTEMA y
/// Administradores conservan control total (cierre ordenado de Windows, mantenimiento).
/// Limitación honesta: un usuario CON credenciales de administrador puede tomar posesión
/// y revertir la ACL; contra eso solo hay evidencia (logs/watchdog), no bloqueo absoluto.
/// </summary>
public static class ProcessProtection
{
    private const int SeKernelObject = 6;
    private const int DaclSecurityInformation = 0x00000004;
    private const uint ProcessTerminate = 0x00000001;
    private const uint StandardRightsAll = 0x001F0000;
    private const uint SpecificRightsAll = 0x0000FFFF;
    private const uint FullControl = StandardRightsAll | SpecificRightsAll; // 0x001FFFFF
    private const int AclRevision = 2;

    /// <summary>
    /// Aplica una DACL que deniega PROCESS_TERMINATE a "Usuarios" y concede control total a
    /// SISTEMA y Administradores. Nunca lanza excepción: en caso de fallo solo loguea.
    /// </summary>
    public static bool DenyTerminateForUsers(Process process, ILoggerService logger, string label)
    {
        IntPtr acl = IntPtr.Zero;
        try
        {
            IntPtr usersSid = GetSid("S-1-5-32-545");
            IntPtr systemSid = GetSid("S-1-5-18");
            IntPtr adminsSid = GetSid("S-1-5-32-544");

            int aclSize = 8
                + (8 + (int)GetLengthSid(usersSid))
                + (8 + (int)GetLengthSid(systemSid))
                + (8 + (int)GetLengthSid(adminsSid));
            acl = Marshal.AllocHGlobal(aclSize);
            if (!InitializeAcl(acl, aclSize, AclRevision)) return Fail(logger, label, "InitializeAcl");
            // AddAccessDeniedAce/AddAccessAllowedAce devuelven BOOL: != 0 = ÉXITO, 0 = fallo.
            // Denegación PRIMERO (precede a las concesiones).
            if (AddAccessDeniedAce(acl, AclRevision, ProcessTerminate, usersSid) == 0) return Fail(logger, label, "AddAccessDeniedAce");
            if (AddAccessAllowedAce(acl, AclRevision, FullControl, systemSid) == 0) return Fail(logger, label, "AddAccessAllowedAce(SYSTEM)");
            if (AddAccessAllowedAce(acl, AclRevision, FullControl, adminsSid) == 0) return Fail(logger, label, "AddAccessAllowedAce(Administradores)");

            if (SetSecurityInfo(new HandleRef(process, process.Handle), SeKernelObject,
                    DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, acl, IntPtr.Zero) != 0)
            {
                return Fail(logger, label, "SetSecurityInfo");
            }

            logger.LogInfo($"Protección anti-finalización aplicada a {label}: un usuario estándar no podrá terminar este proceso.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"No se pudo aplicar la protección anti-finalización a {label}: {ex.Message}");
            return false;
        }
        finally
        {
            if (acl != IntPtr.Zero) Marshal.FreeHGlobal(acl);
        }
    }

    /// <summary>
    /// Restablece la DACL del proceso a NULL (acceso total). Lo usa la limpieza de FFmpeg
    /// huérfanos para poder matar procesos protegidos por la instancia anterior (el
    /// propietario del proceso siempre puede reescribir la DACL). Nunca lanza excepción.
    /// </summary>
    public static bool ResetDacl(Process process, ILoggerService logger)
    {
        try
        {
            return SetSecurityInfo(new HandleRef(process, process.Handle), SeKernelObject,
                DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"No se pudo restablecer la ACL del proceso {process.Id}: {ex.Message}");
            return false;
        }
    }

    private static bool Fail(ILoggerService logger, string label, string step)
    {
        logger.LogWarning($"Protección anti-finalización ({label}): falló en {step} (error {Marshal.GetLastWin32Error()}).");
        return false;
    }

    private static IntPtr GetSid(string stringSid)
    {
        if (!ConvertStringSidToSid(stringSid, out var sid) || sid == IntPtr.Zero)
            throw new InvalidOperationException($"No se pudo convertir el SID {stringSid}");
        return sid;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeAcl(IntPtr pAcl, int nAclLength, int dwAclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int AddAccessDeniedAce(IntPtr pAcl, int dwAceRevision, uint accessMask, IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int AddAccessAllowedAce(IntPtr pAcl, int dwAceRevision, uint accessMask, IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetSecurityInfo(HandleRef handle, int objectType, int securityInfo,
        IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);
}
