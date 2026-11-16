using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace Postinstall.Services
{
    public static class LibreOfficeLanguage
    {
        private const string Namespace = "http://openoffice.org/2001/registry";
        private const string LocalePath = "/org.openoffice.Office.Linguistic/General";
        private const string SharedFileName = "postinstall-language.xcd";
        public static string ProfileFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"LibreOffice\4\user\registrymodifications.xcu");

        public static void EnsureClosed()
        {
            foreach (var name in new[] { "soffice", "soffice.bin" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                        if (!process.HasExited)
                            throw new InvalidOperationException("Zamknij wszystkie okna LibreOffice (także Szybkie uruchamianie) i ponów „Aktualizuj”, aby ustawić polski język.");
        }

        public static bool HasPolishResources(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            var resources = Path.Combine(folder, @"program\resource\pl");
            return Directory.Exists(resources) && Directory.EnumerateFiles(resources, "*.mo", SearchOption.AllDirectories).Any();
        }

        public static bool IsPolishConfigured(string folder)
        {
            try
            {
                if (!HasPolishResources(folder)) return false;
                var shared = Path.Combine(folder, "share", "registry", SharedFileName);
                if (!File.Exists(shared)) return false;
                var settings = Load(shared);
                var manager = Namespaces(settings);
                var value = settings.SelectSingleNode("/oor:data/oor:component-data/node[@oor:name='General']/prop[@oor:name='UILocale']/value", manager);
                if (value == null || value.InnerText != "pl") return false;
                if (!File.Exists(ProfileFile)) return true;
                var profile = Load(ProfileFile);
                var properties = LocaleProperties(profile);
                return properties.Count == 0 || properties.Cast<XmlElement>().All(p => p["value"]?.InnerText == "pl");
            }
            catch (Exception e) when (e is XmlException || e is IOException || e is UnauthorizedAccessException)
            { return false; }
        }

        public static void ConfigurePolish(string folder, SessionLog log)
        {
            EnsureClosed();
            if (!HasPolishResources(folder))
                throw new InvalidOperationException("Brakuje polskich plików interfejsu LibreOffice. Ponów instalację, aby dodać pakiet językowy.");

            var shared = new XmlDocument { PreserveWhitespace = true };
            shared.LoadXml(@"<?xml version=""1.0"" encoding=""utf-8""?>
<oor:data xmlns:oor=""http://openoffice.org/2001/registry"" xmlns:xs=""http://www.w3.org/2001/XMLSchema"">
  <dependency file=""main""/>
  <oor:component-data oor:name=""Linguistic"" oor:package=""org.openoffice.Office"">
    <node oor:name=""General""><prop oor:name=""UILocale"" oor:op=""fuse""><value>pl</value></prop></node>
  </oor:component-data>
</oor:data>");
            Save(shared, Path.Combine(folder, "share", "registry", SharedFileName), null);
            ConfigureProfile(ProfileFile, log);
            if (!IsPolishConfigured(folder))
                throw new InvalidOperationException("Nie potwierdzono polskiego języka interfejsu LibreOffice.");
            log.Write("LibreOffice: ustawiono polski interfejs dla bieżącego użytkownika oraz domyślnie dla nowych profili.");
        }

        public static void ConfigureProfile(string path, SessionLog log)
        {
            var exists = File.Exists(path);
            var document = exists ? Load(path) : new XmlDocument { PreserveWhitespace = true };
            if (!exists) document.LoadXml(@"<?xml version=""1.0"" encoding=""utf-8""?><oor:items xmlns:oor=""http://openoffice.org/2001/registry""/>");
            if (document.DocumentElement?.LocalName != "items" || document.DocumentElement.NamespaceURI != Namespace)
                throw new InvalidOperationException("Nie rozpoznano konfiguracji LibreOffice. Dotychczasowe ustawienia zostały zachowane.");
            var properties = LocaleProperties(document).Cast<XmlElement>().ToList();
            if (properties.Count == 1 && properties[0]["value"]?.InnerText == "pl") return;
            var item = (XmlElement)document.SelectSingleNode("/oor:items/item[@oor:path='" + LocalePath + "']", Namespaces(document));
            if (item == null)
            {
                item = document.CreateElement("item");
                Attribute(item, "path", LocalePath);
                document.DocumentElement.AppendChild(item);
            }
            // Remove only this property; keep all unrelated preferences and document history.
            foreach (var property in properties) property.ParentNode.RemoveChild(property);
            var locale = document.CreateElement("prop");
            Attribute(locale, "name", "UILocale");
            Attribute(locale, "op", "fuse");
            var value = document.CreateElement("value"); value.InnerText = "pl";
            locale.AppendChild(value); item.AppendChild(locale);
            string backup = null;
            if (exists)
            {
                var backups = Path.Combine(SessionLog.DataFolder, "Backups", "LibreOffice");
                Directory.CreateDirectory(backups);
                backup = Path.Combine(backups, "registrymodifications-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".xcu");
            }
            Save(document, path, backup);
            if (backup != null) log.Write("Kopia ustawień LibreOffice: " + backup);
        }

        public static void RemoveSharedDefault(string folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var path = Path.Combine(folder, "share", "registry", SharedFileName);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static XmlNodeList LocaleProperties(XmlDocument document) =>
            document.SelectNodes("/oor:items/item[@oor:path='" + LocalePath + "']/prop[@oor:name='UILocale']", Namespaces(document));

        private static XmlNamespaceManager Namespaces(XmlDocument document)
        { var manager = new XmlNamespaceManager(document.NameTable); manager.AddNamespace("oor", Namespace); return manager; }

        private static void Attribute(XmlElement element, string name, string value)
        { var attribute = element.OwnerDocument.CreateAttribute("oor", name, Namespace); attribute.Value = value; element.Attributes.Append(attribute); }

        private static XmlDocument Load(string path)
        {
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document.Load(reader);
            return document;
        }

        private static void Save(XmlDocument document, string path, string backup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".postinstall-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
                    document.Save(writer);
                if (File.Exists(path)) File.Replace(temporary, path, backup);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
