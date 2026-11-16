using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Threading;

namespace ExtensionCommon.VisualStudio
{
    /// <summary>The folders to scan, and whether the list came from the Extension Manager.</summary>
    internal sealed class ExtensionList
    {
        public ExtensionList(IReadOnlyList<ExtensionSource> sources, bool fromExtensionManager, string problem)
        {
            Sources = sources;
            FromExtensionManager = fromExtensionManager;
            Problem = problem;
        }

        public IReadOnlyList<ExtensionSource> Sources { get; }
        public bool FromExtensionManager { get; }
        public string Problem { get; }
    }

    /// <summary>
    /// Finds the folders of VS itself and of the enabled extensions.
    /// Extensions come from the Extension Manager of VS (IVsExtensionManagerAsync). That interface is not
    /// documented and lives in an assembly that is not in the SDK packages, so it is called by reflection.
    /// When that fails, the extension folders are read directly; that list can contain disabled extensions
    /// and old versions.
    /// </summary>
    internal static class InstalledExtensions
    {
        // A machine-wide extension of a third party gets a random folder name like "qbhos1mt.vs5".
        private static readonly Regex RandomFolder = new Regex(@"^[a-z0-9]{8}\.[a-z0-9]{3}$", RegexOptions.IgnoreCase);

        public static async Task<ExtensionList> GetAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            string ide = GetInstallDirectory(package); // ...\Common7\IDE\
            string userExtensions = GetUserExtensionsFolder(package);

            List<(string Folder, string Name)> enabled = null;
            HashSet<string> installed = null;
            string problem = null;
            try
            {
                var manager = await package.GetServiceAsync(FindServiceType());
                (enabled, installed) = await QueryManagerAsync(manager);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                problem = ex.GetType().Name + ": " + ex.Message;
            }

            await TaskScheduler.Default; // folder walking is slow; off the UI thread
            var sources = new List<ExtensionSource>();
            var thirdParty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (enabled != null)
            {
                foreach (var (folder, name) in enabled)
                {
                    if (IsVisualStudioFolder(folder, ide))
                        continue; // added below with all other VS folders
                    sources.Add(new ExtensionSource(folder, name, false));
                    thirdParty.Add(Normalize(folder));
                }
            }
            else
            {
                // Depth 3: an extension deployed by F5 lives in <publisher>\<name>\<version>, not in a random folder.
                foreach (string folder in ManifestFolders(userExtensions, 3))
                {
                    sources.Add(new ExtensionSource(folder, null, false));
                    thirdParty.Add(Normalize(folder));
                }
            }

            foreach (string folder in ManifestFolders(ide, 6))
            {
                string key = Normalize(folder);
                if (thirdParty.Contains(key))
                    continue;
                if (!IsVisualStudioFolder(folder, ide))
                {
                    // Machine-wide extension of a third party. With the manager: only when installed (and then
                    // it was in the enabled list, or it is disabled). Without: always.
                    if (installed == null)
                        sources.Add(new ExtensionSource(folder, null, false));
                    continue;
                }
                sources.Add(new ExtensionSource(folder, ExtensionSource.VisualStudioName, true));
            }

            return new ExtensionList(sources, enabled != null, problem);
        }

        private static string GetInstallDirectory(IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var shell = services.GetService(typeof(SVsShell)) as IVsShell ?? throw new InvalidOperationException("SVsShell is not available.");
            ErrorHandler.ThrowOnFailure(shell.GetProperty((int)__VSSPROPID.VSSPROPID_InstallDirectory, out object dir));
            return (string)dir;
        }

        private static string GetUserExtensionsFolder(IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var settings = new ShellSettingsManager(services);
            return settings.GetApplicationDataFolder(ApplicationDataFolder.UserExtensions);
        }

        /// <summary>VS's own: anything under Common7\IDE except a random-named folder directly in IDE\Extensions.</summary>
        internal static bool IsVisualStudioFolder(string folder, string ide)
        {
            string f = Normalize(folder), root = Normalize(ide);
            if (!f.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return false;
            string relative = f.Substring(root.Length).Trim('\\');
            var parts = relative.Split('\\');
            return !(parts.Length == 2 && parts[0].Equals("Extensions", StringComparison.OrdinalIgnoreCase) && RandomFolder.IsMatch(parts[1]));
        }

        private static IEnumerable<string> ManifestFolders(string root, int depth)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return Enumerable.Empty<string>();
            var found = new List<string>();
            Walk(root, depth, found);
            return found;
        }

        private static void Walk(string folder, int depth, List<string> found)
        {
            try
            {
                if (File.Exists(Path.Combine(folder, "extension.vsixmanifest")))
                {
                    found.Add(folder);
                    return;
                }
                if (depth == 0)
                    return;
                foreach (string sub in Directory.EnumerateDirectories(folder))
                    Walk(sub, depth - 1, found);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\') + "\\";

        // ---- Extension Manager by reflection

        private static Type FindServiceType()
        {
            const string name = "Microsoft.VisualStudio.ExtensionManager.SVsExtensionManager";
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "Microsoft.VisualStudio.ExtensionManager")
                {
                    var type = assembly.GetType(name);
                    if (type != null)
                        return type;
                }
            }
            // VS's own assembly, not someone else's: loading it is safe. Only its GUID is used.
            var loaded = Assembly.Load("Microsoft.VisualStudio.ExtensionManager");
            return loaded.GetType(name, throwOnError: true);
        }

        private static async Task<(List<(string, string)> Enabled, HashSet<string> Installed)> QueryManagerAsync(object manager)
        {
            if (manager == null)
                throw new InvalidOperationException("SVsExtensionManager is not available.");
            var api = manager.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IVsExtensionManagerAsync")
                      ?? throw new InvalidOperationException("IVsExtensionManagerAsync is not implemented.");

            var enabled = new List<(string, string)>();
            foreach (object e in await CallAsync(api, manager, "GetEnabledExtensionsAsync"))
            {
                string path = Get(e, "InstallPath") as string;
                object header = Get(e, "Header");
                string name = Get(header, "LocalizedName") as string ?? Get(header, "Name") as string;
                if (!string.IsNullOrEmpty(path))
                    enabled.Add((path, name));
            }

            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object e in await CallAsync(api, manager, "GetInstalledExtensionsAsync"))
                if (Get(e, "InstallPath") is string path && path.Length > 0)
                    installed.Add(Normalize(path));

            return (enabled, installed);
        }

        private static async Task<IEnumerable> CallAsync(Type api, object target, string method)
        {
            // In VS 2026 18.13 the CancellationToken is required, not optional. Accept both forms.
            var info = api.GetMethods().FirstOrDefault(m => m.Name == method
                           && m.GetParameters().All(p => p.IsOptional || p.ParameterType == typeof(System.Threading.CancellationToken)))
                       ?? throw new MissingMethodException(api.FullName, method);
            var args = info.GetParameters().Select(p => p.ParameterType == typeof(System.Threading.CancellationToken)
                ? (object)System.Threading.CancellationToken.None
                : p.DefaultValue is DBNull ? null : p.DefaultValue).ToArray();
            var task = (Task)info.Invoke(target, args);
            await task.ConfigureAwait(true);
            return (IEnumerable)task.GetType().GetProperty("Result").GetValue(task) ?? Enumerable.Empty<object>();
        }

        /// <summary>A property by name, also when the class implements it explicitly through an interface.</summary>
        private static object Get(object target, string property)
        {
            if (target == null)
                return null;
            var type = target.GetType();
            var info = type.GetProperty(property)
                       ?? type.GetInterfaces().Select(i => i.GetProperty(property)).FirstOrDefault(p => p != null);
            return info?.GetValue(target);
        }
    }
}