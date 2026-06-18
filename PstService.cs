using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace PstMerger
{
    public class PstService
    {
        // Stores hashes of items already copied into the destination PST.
        // Key format: "FolderName|Hash" — folder-agnostic dedup (same item in
        // any folder counts as a duplicate of the same item elsewhere).
        private readonly HashSet<string> _seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int _duplicatesSkipped = 0;
        private bool _removeDuplicates = false;

        public void MergeFiles(
            string[] sourceFiles,
            string destinationPst,
            System.Threading.CancellationToken ct,
            Action<int, string> onProgress,
            bool removeDuplicates = false)
        {
            _removeDuplicates = removeDuplicates;
            _seenHashes.Clear();
            _duplicatesSkipped = 0;

            Outlook.Application outlookApp = null;
            Outlook.NameSpace ns = null;
            Outlook.Folder destRoot = null;

            try
            {
                outlookApp = new Outlook.Application();
                ns = outlookApp.GetNamespace("MAPI");

                // 1. Ensure the destination PST exists or create it
                if (!File.Exists(destinationPst))
                {
                    onProgress(0, "Creating destination PST...");
                    ns.AddStore(destinationPst);
                }
                else
                {
                    onProgress(0, "Opening existing destination PST...");
                    ns.AddStore(destinationPst);
                }

                // Get the destination root folder
                destRoot = GetRootFolder(ns, destinationPst, onProgress);
                if (destRoot == null) throw new Exception("Could not find destination root.");

                // If deduplication is on, pre-seed hashes from destination PST
                // so we don't duplicate items already present in an existing master PST.
                if (_removeDuplicates && File.Exists(destinationPst))
                {
                    onProgress(0, "Scanning destination PST for existing items (dedup pre-seed)...");
                    SeedHashesFromFolder(destRoot, onProgress);
                    onProgress(0, string.Format("Pre-seed complete. {0} existing items indexed.", _seenHashes.Count));
                }

                int count = 0;
                foreach (string sourceFile in sourceFiles)
                {
                    if (ct.IsCancellationRequested) break;

                    // Skip if it's the destination itself
                    if (string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(destinationPst), StringComparison.OrdinalIgnoreCase))
                        continue;

                    count++;
                    onProgress(count, string.Format("Merging: {0}", Path.GetFileName(sourceFile)));

                    ProcessSourcePst(ns, sourceFile, destRoot, ct, onProgress);
                }

                ns.RemoveStore(destRoot);

                if (_removeDuplicates)
                {
                    onProgress(0, string.Format("Deduplication complete. {0} duplicate item(s) skipped.", _duplicatesSkipped));
                }
            }
            finally
            {
                if (ns != null) Marshal.ReleaseComObject(ns);
                if (outlookApp != null) Marshal.ReleaseComObject(outlookApp);
            }
        }

        // ------------------------------------------------------------------
        // Pre-seeds _seenHashes by walking an existing destination folder tree.
        // Uses hash-only keys (no folder path) so duplicates are detected
        // regardless of which folder they live in.
        // ------------------------------------------------------------------
        private void SeedHashesFromFolder(Outlook.Folder folder, Action<int, string> onProgress)
        {
            Outlook.Items items = folder.Items;
            int count = items.Count;

            for (int i = 1; i <= count; i++)
            {
                object item = null;
                try
                {
                    item = items[i];
                    string hash = GetItemHash(item);
                    if (!string.IsNullOrEmpty(hash))
                        _seenHashes.Add(hash);
                }
                catch { /* ignore individual item errors during seeding */ }
                finally
                {
                    if (item != null) Marshal.ReleaseComObject(item);
                }
            }
            Marshal.ReleaseComObject(items);

            Outlook.Folders subFolders = folder.Folders;
            foreach (Outlook.Folder sub in subFolders)
            {
                SeedHashesFromFolder(sub, onProgress);
                Marshal.ReleaseComObject(sub);
            }
            Marshal.ReleaseComObject(subFolders);
        }

        // ------------------------------------------------------------------
        // Generates a deterministic fingerprint for any Outlook item type.
        // Fields: Subject, SenderName/From, SentOn/CreationTime, Size, BodyLen.
        // These are stable across PST copies and cover all common item types.
        // ------------------------------------------------------------------
        private string GetItemHash(object item)
        {
            try
            {
                string subject = "";
                string sender = "";
                string sentOn = "";
                string size = "";
                string bodyLen = "";

                dynamic dyn = item;

                // Subject — present on almost every item type
                try { subject = (string)dyn.Subject ?? ""; } catch { }

                // Sender / organiser / owner depending on type
                try { sender = (string)dyn.SenderName ?? ""; }
                catch
                {
                    try { sender = (string)dyn.Organizer ?? ""; }
                    catch { try { sender = (string)dyn.From ?? ""; } catch { } }
                }

                // Sent / created timestamp
                try { sentOn = ((DateTime)dyn.SentOn).ToString("o"); }
                catch
                {
                    try { sentOn = ((DateTime)dyn.Start).ToString("o"); }
                    catch { try { sentOn = ((DateTime)dyn.CreationTime).ToString("o"); } catch { } }
                }

                // Item size in bytes
                try { size = ((int)dyn.Size).ToString(); } catch { }

                // Body length as secondary discriminator
                try { bodyLen = ((string)dyn.Body ?? "").Length.ToString(); } catch { }

                string raw = string.Join("|", subject, sender, sentOn, size, bodyLen);
                using (var md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    return BitConverter.ToString(bytes).Replace("-", "");
                }
            }
            catch
            {
                return null; // If we can't hash, let the item through
            }
        }

        // ------------------------------------------------------------------
        private void ProcessSourcePst(Outlook.NameSpace ns, string filePath, Outlook.Folder destRoot, System.Threading.CancellationToken ct, Action<int, string> onProgress)
        {
            Outlook.Folder sourceRoot = null;
            try
            {
                ns.AddStore(filePath);
                sourceRoot = GetRootFolder(ns, filePath, onProgress);
                if (sourceRoot == null) return;

                CopyFolders(sourceRoot, destRoot, ct, onProgress);

                ns.RemoveStore(sourceRoot);
                Marshal.ReleaseComObject(sourceRoot);
            }
            catch (Exception ex)
            {
                onProgress(-1, string.Format("Error processing {0}: {1}", Path.GetFileName(filePath), ex.Message));
            }
        }

        // ------------------------------------------------------------------
        // Recursively copies folders. Dedup uses hash-only keys so a duplicate
        // email in Inbox vs. Sent Items is still correctly detected.
        // ------------------------------------------------------------------
        private void CopyFolders(
            Outlook.Folder sourceFolder,
            Outlook.Folder destFolder,
            System.Threading.CancellationToken ct,
            Action<int, string> onProgress)
        {
            if (ct.IsCancellationRequested) return;

            // 1. Copy items in the current folder
            Outlook.Items sourceItems = sourceFolder.Items;
            int itemCount = sourceItems.Count;

            for (int i = itemCount; i >= 1; i--)
            {
                if (ct.IsCancellationRequested) break;

                object item = null;
                dynamic copy = null;
                try
                {
                    item = sourceItems[i];

                    // ----- Deduplication check (hash only — no folder path) -----
                    if (_removeDuplicates)
                    {
                        string hash = GetItemHash(item);
                        if (!string.IsNullOrEmpty(hash))
                        {
                            if (_seenHashes.Contains(hash))
                            {
                                _duplicatesSkipped++;
                                continue;
                            }
                            _seenHashes.Add(hash);
                        }
                    }
                    // ------------------------------------------------------------

                    dynamic dynItem = item;
                    copy = dynItem.Copy();
                    copy.Move(destFolder);
                }
                catch (Exception ex)
                {
                    onProgress(-1, string.Format("Warning: Failed to copy item in {0}: {1}", sourceFolder.Name, ex.Message));
                }
                finally
                {
                    if (copy != null) Marshal.ReleaseComObject(copy);
                    if (item != null) Marshal.ReleaseComObject(item);
                }
            }
            if (sourceItems != null) Marshal.ReleaseComObject(sourceItems);

            // 2. Recursively process subfolders
            Outlook.Folders sourceSubFolders = sourceFolder.Folders;
            foreach (Outlook.Folder sourceSubFolder in sourceSubFolders)
            {
                if (ct.IsCancellationRequested) break;

                Outlook.Folder destSubFolder = null;
                Outlook.Folders destFolders = destFolder.Folders;

                int maxRetries = 3;
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        destSubFolder = FindFolderByName(destFolders, sourceSubFolder.Name);

                        if (destSubFolder == null)
                        {
                            try
                            {
                                destSubFolder = destFolders.Add(sourceSubFolder.Name, sourceSubFolder.DefaultItemType) as Outlook.Folder;
                            }
                            catch
                            {
                                destSubFolder = destFolders.Add(sourceSubFolder.Name) as Outlook.Folder;
                            }
                        }
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == maxRetries)
                            onProgress(-1, string.Format("Error creating folder {0} after {1} attempts: {2}", sourceSubFolder.Name, maxRetries, ex.Message));
                        else
                        {
                            onProgress(-1, string.Format("Retry {0}/{1} for folder {2}: {3}", attempt, maxRetries, sourceSubFolder.Name, ex.Message));
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                }

                if (destSubFolder != null)
                {
                    CopyFolders(sourceSubFolder, destSubFolder, ct, onProgress);
                    Marshal.ReleaseComObject(destSubFolder);
                }

                if (destFolders != null) Marshal.ReleaseComObject(destFolders);
                if (sourceSubFolder != null) Marshal.ReleaseComObject(sourceSubFolder);
            }
            if (sourceSubFolders != null) Marshal.ReleaseComObject(sourceSubFolders);
        }

        // ------------------------------------------------------------------
        private Outlook.Folder FindFolderByName(Outlook.Folders folders, string name)
        {
            foreach (Outlook.Folder f in folders)
            {
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
                Marshal.ReleaseComObject(f);
            }
            return null;
        }

        // ------------------------------------------------------------------
        private Outlook.Folder GetRootFolder(Outlook.NameSpace ns, string filePath, Action<int, string> onProgress)
        {
            Outlook.Store targetStore = null;
            foreach (Outlook.Store store in ns.Stores)
            {
                if (string.Equals(store.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    targetStore = store;
                    break;
                }
            }

            if (targetStore != null)
            {
                try
                {
                    const string PR_IPM_SUBTREE_ENTRYID = "http://schemas.microsoft.com/mapi/proptag/0x35E00102";
                    object ipmProp = targetStore.PropertyAccessor.GetProperty(PR_IPM_SUBTREE_ENTRYID);

                    string ipmEntryId = null;
                    if (ipmProp is string)
                        ipmEntryId = (string)ipmProp;
                    else if (ipmProp is byte[])
                        ipmEntryId = BitConverter.ToString((byte[])ipmProp).Replace("-", "");

                    if (!string.IsNullOrEmpty(ipmEntryId))
                    {
                        var ipmRoot = ns.GetFolderFromID(ipmEntryId, targetStore.StoreID) as Outlook.Folder;
                        if (ipmRoot != null) return ipmRoot;
                    }
                }
                catch { }
            }

            // Fallback: legacy loop
            foreach (Outlook.Folder folder in ns.Folders)
            {
                try
                {
                    if (folder.Store != null &&
                        string.Equals(folder.Store.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                        return folder;
                }
                catch { }
                Marshal.ReleaseComObject(folder);
            }

            return null;
        }
    }
}
