using System.Text;
using System.Text.Json;

namespace Agent080.Utilities.Serialization
{
    /// <summary>
    /// Converts property names to snake_case for JSON serialization
    /// </summary>
    public class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var result = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]))
                {
                    result.Append('_');
                }
                result.Append(char.ToLower(name[i]));
            }
            return result.ToString();
        }
    }
}