using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace RokidControl.Core.Navigation;

public static class LauncherIndicatorLocator
{
    public static DevicePoint? Locate(string xml, LauncherShortcut shortcut, int width, int height)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var matches = new List<DevicePoint>();
            foreach (var node in document.Descendants("node"))
            {
                if ((string?)node.Attribute("package") != "com.rokid.os.sprite.launcher" ||
                    (string?)node.Attribute("resource-id") != "com.rokid.os.sprite.launcher:id/indicator" ||
                    (string?)node.Attribute("enabled") != "true" || (string?)node.Attribute("clickable") != "true") continue;
                var match = Regex.Match((string?)node.Attribute("bounds") ?? "", @"^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$");
                if (!match.Success) continue;
                var values = match.Groups.Cast<Group>().Skip(1).Select(g => int.Parse(g.Value)).ToArray();
                var (left, top, right, bottom) = (values[0], values[1], values[2], values[3]);
                if (left >= right || top >= bottom || right > width || bottom > height) continue;
                matches.Add(new DevicePoint((left + right) / 2 + (int)shortcut * (right - left) / 3, (top + bottom) / 2));
            }
            return matches.Count == 1 ? matches[0] : null;
        }
        catch (Exception exception) when (exception is XmlException or FormatException or OverflowException)
        { return null; }
    }
}
