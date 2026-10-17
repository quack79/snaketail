using System.Text.Json;
using System.Text.RegularExpressions;
using SnakeTail;

namespace LongMaidDisplayPlugin
{
    /// <summary>
    /// DragonMaid example plugin: expands skills: <number> by appending the skill name.
    /// </summary>
    public sealed class DragonMaidDisplayPlugin : ILogDisplayPlugin, ILogDisplayBlockPlugin
    {
        // Matches the three single-value keys skills/passive_skill/aura_skills, with optional double quotes.
        private static readonly Regex SkillRegex = new Regex(@"(?<key>""?(?:skills|passive_skill|aura_skills)""?)\s*:\s*(?<id>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Matches a single-line skill list skill/skills: [1,2,3], with optional double quotes.
        private static readonly Regex SkillListRegex = new Regex(@"(?<key>""?(?:skill|skills)""?)\s*:\s*\[(?<ids>\s*\d+(?:\s*,\s*\d+)*)\s*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Matches the start line of a skills array (for collecting multi-line "skills": [ ... ] blocks).
        private static readonly Regex SkillsArrayStartRegex = new Regex(@"^\s*""?(?:skills|skill)""?\s*:\s*\[\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
        // Matches the end line of a skills array.
        private static readonly Regex SkillsArrayEndRegex = new Regex(@"^\s*\]\s*,?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Matches a number line inside a skills array (trailing comma allowed).
        private static readonly Regex SkillsArrayIdLineRegex = new Regex(@"^(?<prefix>\s*)(?<id>\d+)(?<suffix>\s*,?\s*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Matches the start line of a battle power attribute block.
        private static readonly Regex BattleEffectsBlockStartRegex = new Regex(@"^\s*attr_data=effects\s*\{\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Matches the lines allowed inside a battle power attribute block.
        private static readonly Regex BattleEffectsBlockLineRegex = new Regex(@"^\s*(attr_data=effects|effects)\s*\{\s*$|^\s*key:\s*\d+\s*$|^\s*value:\s*\d+\s*$|^\s*\}\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Matches the timestamp header of the next log entry, used to split block boundaries.
        private static readonly Regex TimestampHeadRegex = new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\t", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Matches key lines inside a battle power attribute block.
        private static readonly Regex BattleEffectsKeyRegex = new Regex(@"(?m)^(?<indent>\s*key:\s*)(?<id>\d+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private readonly Dictionary<int, string> _skillMap = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _battlePowerMap = new Dictionary<int, string>();
        private string _jsonPath = string.Empty;
        private string _battlePowerJsonPath = string.Empty;

        public string Name => "DragonMaid";

        /// <summary>
        /// Initializes and reads s_skill.json from the same directory.
        /// </summary>
        public void Initialize(PluginContext context)
        {
            _skillMap.Clear();
            _battlePowerMap.Clear();
            _jsonPath = Path.Combine(context.PluginDirectoryAbsolutePath, "s_skill.json");
            _battlePowerJsonPath = Path.Combine(context.PluginDirectoryAbsolutePath, "s_battle_power.json");
            if (!File.Exists(_jsonPath))
                throw new FileNotFoundException("s_skill.json not found", _jsonPath);

            string jsonText = File.ReadAllText(_jsonPath);
            using JsonDocument document = JsonDocument.Parse(jsonText);
            if (!document.RootElement.TryGetProperty("s_skill", out JsonElement skillsElement) || skillsElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("JSON is missing the s_skill array");

            foreach (JsonElement row in skillsElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2)
                    continue;

                JsonElement idElement = row[0];
                JsonElement nameElement = row[1];
                if (idElement.ValueKind != JsonValueKind.Number || nameElement.ValueKind != JsonValueKind.String)
                    continue;

                if (!idElement.TryGetInt32(out int skillId))
                    continue;

                string? skillName = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(skillName))
                    continue;

                _skillMap[skillId] = skillName;
            }

            // s_battle_power.json is optional; when present, it enables key mapping for effects blocks.
            if (File.Exists(_battlePowerJsonPath))
            {
                string battleJsonText = File.ReadAllText(_battlePowerJsonPath);
                using JsonDocument battleDocument = JsonDocument.Parse(battleJsonText);
                if (!battleDocument.RootElement.TryGetProperty("s_battle_power", out JsonElement battlePowerElement) || battlePowerElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("s_battle_power.json is missing the s_battle_power array");

                foreach (JsonElement row in battlePowerElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6)
                        continue;

                    JsonElement idElement = row[0];
                    JsonElement nameElement = row[5];
                    if (idElement.ValueKind != JsonValueKind.Number || nameElement.ValueKind != JsonValueKind.String)
                        continue;

                    if (!idElement.TryGetInt32(out int battlePowerId))
                        continue;

                    string? battlePowerName = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(battlePowerName))
                        continue;

                    _battlePowerMap[battlePowerId] = battlePowerName;
                }
            }
        }

        /// <summary>
        /// When an attr_data=effects start line matches, collects the complete effects attribute block that follows.
        /// </summary>
        public bool TryCollectBlock(int lineKey, string currentLine, Func<int, string> readLineByLineKey, out string blockText)
        {
            blockText = string.Empty;
            if (string.IsNullOrEmpty(currentLine))
                return false;

            if (!BattleEffectsBlockStartRegex.IsMatch(currentLine))
            {
                // Multi-line skills array: collect from the "skills": [ line to the ] line.
                if (!SkillsArrayStartRegex.IsMatch(currentLine))
                    return false;

                const int maxSkillsBlockLines = 300;
                List<string> skillLines = new List<string>(32) { currentLine };
                bool closed = false;
                for (int offset = 1; offset < maxSkillsBlockLines; offset++)
                {
                    string nextLine = readLineByLineKey(lineKey + offset);
                    if (string.IsNullOrEmpty(nextLine))
                        break;

                    if (TimestampHeadRegex.IsMatch(nextLine))
                        break;

                    skillLines.Add(nextLine);
                    if (SkillsArrayEndRegex.IsMatch(nextLine))
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed)
                    return false;

                blockText = string.Join(Environment.NewLine, skillLines);
                return true;
            }

            const int maxBlockLines = 600;
            List<string> lines = new List<string>(64) { currentLine };
            for (int offset = 1; offset < maxBlockLines; offset++)
            {
                string nextLine = readLineByLineKey(lineKey + offset);
                if (string.IsNullOrEmpty(nextLine))
                    break;

                if (TimestampHeadRegex.IsMatch(nextLine))
                    break;

                if (!BattleEffectsBlockLineRegex.IsMatch(nextLine))
                    break;

                lines.Add(nextLine);
            }

            if (lines.Count <= 1)
                return false;

            blockText = string.Join(Environment.NewLine, lines);
            return true;
        }

        /// <summary>
        /// Only processes lines containing skills/passive_skill/aura_skills with a number.
        /// </summary>
        public bool CanProcess(string line)
        {
            if (string.IsNullOrEmpty(line))
                return false;

            // Supports both single-line skill mapping and multi-line effects block mapping inputs.
            return SkillRegex.IsMatch(line)
                || SkillListRegex.IsMatch(line)
                || (line.Contains(Environment.NewLine) && SkillsArrayStartRegex.IsMatch(line))
                || (line.Contains(Environment.NewLine) && BattleEffectsKeyRegex.IsMatch(line));
        }

        /// <summary>
        /// On a match with a mapping, outputs key: ID name; otherwise passes through to later plugins.
        /// </summary>
        public PluginProcessResult TryProcess(string line)
        {
            if (string.IsNullOrEmpty(line))
                return new PluginProcessResult { Handled = false, Output = line };

            // Multi-line effects block: map key: <number> to key: <number> <name>.
            if (line.Contains(Environment.NewLine) && BattleEffectsKeyRegex.IsMatch(line))
            {
                string blockOutput = BattleEffectsKeyRegex.Replace(line, match =>
                {
                    string idText = match.Groups["id"].Value;
                    if (!int.TryParse(idText, out int keyId))
                        return match.Value;

                    if (!_battlePowerMap.TryGetValue(keyId, out string? keyName))
                        return match.Value;

                    string indent = match.Groups["indent"].Value;
                    return string.Format("{0}{1} {2}", indent, keyId, keyName);
                });

                bool changed = !string.Equals(blockOutput, line, StringComparison.Ordinal);
                return new PluginProcessResult
                {
                    Handled = changed,
                    Output = blockOutput,
                    ErrorMessage = !changed && _battlePowerMap.Count == 0 ? "s_battle_power mapping not loaded; effects key names cannot be expanded" : string.Empty
                };
            }

            // Multi-line skills array: expand each number line to "number name".
            if (line.Contains(Environment.NewLine) && SkillsArrayStartRegex.IsMatch(line))
            {
                string[] lines = line.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    Match idLineMatch = SkillsArrayIdLineRegex.Match(lines[i]);
                    if (!idLineMatch.Success)
                        continue;

                    string arrayIdText = idLineMatch.Groups["id"].Value;
                    if (!int.TryParse(arrayIdText, out int arraySkillId))
                        continue;

                    if (!_skillMap.TryGetValue(arraySkillId, out string? arraySkillName))
                        continue;

                    lines[i] = string.Format("{0}{1} {2}{3}",
                        idLineMatch.Groups["prefix"].Value,
                        arraySkillId,
                        arraySkillName,
                        idLineMatch.Groups["suffix"].Value);
                    changed = true;
                }

                if (!changed)
                    return new PluginProcessResult { Handled = false, Output = line };

                return new PluginProcessResult
                {
                    Handled = true,
                    Output = string.Join(Environment.NewLine, lines)
                };
            }

            // skill/skills: [number,number]: map each ID and append the skill name in place.
            Match listMatch = SkillListRegex.Match(line);
            if (listMatch.Success)
            {
                string idsText = listMatch.Groups["ids"].Value;
                string[] idTokens = idsText.Split(',');
                bool changed = false;
                string[] mappedTokens = new string[idTokens.Length];
                for (int i = 0; i < idTokens.Length; i++)
                {
                    string token = idTokens[i].Trim();
                    if (!int.TryParse(token, out int listSkillId))
                    {
                        mappedTokens[i] = token;
                        continue;
                    }

                    if (_skillMap.TryGetValue(listSkillId, out string? listSkillName))
                    {
                        mappedTokens[i] = string.Format("{0} {1}", listSkillId, listSkillName);
                        changed = true;
                        continue;
                    }

                    mappedTokens[i] = token;
                }

                if (!changed)
                    return new PluginProcessResult { Handled = false, Output = line };

                string listKey = listMatch.Groups["key"].Value;
                string listReplacement = string.Format("{0}: [{1}]", listKey, string.Join(",", mappedTokens));
                string listOutput = SkillListRegex.Replace(line, listReplacement, 1);
                return new PluginProcessResult { Handled = true, Output = listOutput };
            }

            Match match = SkillRegex.Match(line);
            if (!match.Success)
                return new PluginProcessResult { Handled = false, Output = line };

            string key = match.Groups["key"].Value;
            string idText = match.Groups["id"].Value;
            if (!int.TryParse(idText, out int skillId))
            {
                return new PluginProcessResult
                {
                    Handled = false,
                    Output = line,
                    ErrorMessage = key + " failed to parse number: " + idText
                };
            }

            if (!_skillMap.TryGetValue(skillId, out string? skillName))
                return new PluginProcessResult { Handled = false, Output = line };

            // Keep the original key name; only append the skill name.
            string replacement = string.Format("{0}: {1} {2}", key, skillId, skillName);
            string output = SkillRegex.Replace(line, replacement, 1);
            return new PluginProcessResult { Handled = true, Output = output };
        }
    }
}
