using System;
using System.IO;
using WixToolset.Dtf.WindowsInstaller;

namespace ForzaGallerySync.CA
{
    /// <summary>
    /// DTF managed custom actions for the Forza Gallery Sync MSI.
    /// </summary>
    public static class CustomActions
    {
        /// <summary>
        /// Uninstall-only: move the database files out of the install directory
        /// into %APPDATA%\forza-sync so user data survives uninstall / upgrade.
        /// Deferred custom action; receives "installDir|dataDir" via
        /// CustomActionData (set by the type-51 SetPreserveData action).
        /// Return="ignore" in the MSI, so failures never block uninstall;
        /// untracked DB files are preserved in the leftover folder as a fallback.
        /// </summary>
        [CustomAction]
        public static ActionResult PreserveDatabase(Session session)
        {
            session.Log("FGS.CA: PreserveDatabase begin");
            try
            {
                string data = session["CustomActionData"];
                string[] parts = (data ?? "").Split('|');
                if (parts.Length < 2)
                {
                    session.Log("FGS.CA: unexpected CustomActionData; skipping preserve.");
                    return ActionResult.Success;
                }
                string installDir = parts[0];
                string dataDir = parts[1];

                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                {
                    session.Log("FGS.CA: install dir not found: {0}", installDir);
                    return ActionResult.Success;
                }

                try { Directory.CreateDirectory(dataDir); }
                catch (Exception ex) { session.Log("FGS.CA: create data dir failed: {0}", ex); return ActionResult.Success; }

                int moved = 0;
                foreach (var name in new[] { "forza_sync.db", "forza_sync.db-wal", "forza_sync.db-shm" })
                {
                    var src = Path.Combine(installDir, name);
                    var dst = Path.Combine(dataDir, name);
                    try
                    {
                        if (File.Exists(src) && !File.Exists(dst))
                        {
                            File.Move(src, dst);
                            moved++;
                            session.Log("FGS.CA: preserved {0} -> {1}", src, dst);
                        }
                    }
                    catch (Exception ex)
                    {
                        session.Log("FGS.CA: preserve {0} failed: {1}", name, ex);
                    }
                }
                session.Log("FGS.CA: preserved {0} file(s)", moved);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("FGS.CA: unexpected error: {0}", ex);
                return ActionResult.Success; // never block uninstall
            }
        }
    }
}
