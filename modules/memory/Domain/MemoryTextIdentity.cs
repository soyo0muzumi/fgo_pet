using System.Text;
using System.Text.RegularExpressions;

namespace FgoPet.Core.Memory;

public static class MemoryTextIdentity
{
    public static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");
}
