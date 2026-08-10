using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// SQL 模板服务。
    /// 管理用户自定义的 SQL 代码片段模板，支持缩写展开。
    /// 模板存储在部署目录下的 sql_templates.json 文件中。
    ///
    /// 模板语法：
    ///   缩写 → 展开内容
    ///   $cursor 表示展开后光标的位置
    ///
    /// 示例：
    ///   sf → SELECT TOP 100 * FROM $cursor WITH(NOLOCK)
    ///   ssf → SELECT * FROM $cursor
    ///   ct → CREATE TABLE $cursor (
    ///        )
    /// </summary>
    public class SqlTemplateService
    {
        private readonly ILoggerService _logger;
        private readonly string _configDir;
        private readonly string _templatesFilePath;
        private List<SqlTemplate> _templates = new();
        private Dictionary<string, SqlTemplate> _templateLookup = new(StringComparer.OrdinalIgnoreCase);

        public SqlTemplateService(ILoggerService logger, string configDir)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configDir = configDir ?? throw new ArgumentNullException(nameof(configDir));
            _templatesFilePath = Path.Combine(configDir, "sql_templates.json");
        }

        /// <summary>
        /// 加载模板（从文件，如果文件不存在则加载默认模板并保存）。
        /// </summary>
        public void Load()
        {
            try
            {
                if (File.Exists(_templatesFilePath))
                {
                    var json = File.ReadAllText(_templatesFilePath);
                    _templates = JsonConvert.DeserializeObject<List<SqlTemplate>>(json) ?? new List<SqlTemplate>();
                    _logger.Information($"[SqlTemplate] 已加载 {_templates.Count} 个模板: {_templatesFilePath}");
                }
                else
                {
                    _templates = GetDefaultTemplates();
                    Save();
                    _logger.Information($"[SqlTemplate] 首次使用，已创建默认模板: {_templates.Count} 个");
                }

                RebuildLookup();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 加载模板失败，使用默认模板");
                _templates = GetDefaultTemplates();
                RebuildLookup();
            }
        }

        /// <summary>
        /// 保存模板到文件。
        /// </summary>
        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_templates, Formatting.Indented);
                File.WriteAllText(_templatesFilePath, json);
                _logger.Information($"[SqlTemplate] 已保存 {_templates.Count} 个模板");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[SqlTemplate] 保存模板失败");
            }
        }

        /// <summary>
        /// 获取所有模板（按缩写排序）。
        /// </summary>
        public List<SqlTemplate> GetAllTemplates()
        {
            return _templates.OrderBy(t => t.Abbreviation, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 添加或更新模板。
        /// </summary>
        public void AddOrUpdate(string abbreviation, string expansion)
        {
            var existing = _templates.FirstOrDefault(t =>
                string.Equals(t.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Expansion = expansion;
            }
            else
            {
                _templates.Add(new SqlTemplate { Abbreviation = abbreviation, Expansion = expansion });
            }

            Save();
            RebuildLookup();
        }

        /// <summary>
        /// 删除模板。
        /// </summary>
        public bool Delete(string abbreviation)
        {
            var removed = _templates.RemoveAll(t =>
                string.Equals(t.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                Save();
                RebuildLookup();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 尝试根据缩写获取模板展开内容。
        /// 返回 null 表示未找到匹配的模板。
        /// </summary>
        public string? TryExpand(string abbreviation)
        {
            if (_templateLookup.TryGetValue(abbreviation, out var template))
            {
                return template.Expansion;
            }
            return null;
        }

        private void RebuildLookup()
        {
            _templateLookup = _templates
                .Where(t => !string.IsNullOrWhiteSpace(t.Abbreviation))
                .ToDictionary(t => t.Abbreviation!, t => t, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 默认模板列表。
        /// </summary>
        private static List<SqlTemplate> GetDefaultTemplates()
        {
            return new List<SqlTemplate>
            {
                new SqlTemplate
                {
                    Abbreviation = "sf",
                    Expansion = "SELECT TOP 100 * FROM $cursor WITH(NOLOCK)"
                },
                new SqlTemplate
                {
                    Abbreviation = "ssf",
                    Expansion = "SELECT * FROM $cursor WITH(NOLOCK)"
                },
                new SqlTemplate
                {
                    Abbreviation = "ct",
                    Expansion = "CREATE TABLE $cursor (\n    \n);"
                },
                new SqlTemplate
                {
                    Abbreviation = "sp",
                    Expansion = "CREATE PROCEDURE $cursor\nAS\nBEGIN\n    \nEND"
                },
                new SqlTemplate
                {
                    Abbreviation = "sv",
                    Expansion = "CREATE VIEW $cursor\nAS\nSELECT "
                },
                new SqlTemplate
                {
                    Abbreviation = "sfu",
                    Expansion = "CREATE FUNCTION $cursor\nRETURNS \nAS\nBEGIN\n    RETURN \nEND"
                },
                new SqlTemplate
                {
                    Abbreviation = "ij",
                    Expansion = "INNER JOIN $cursor ON "
                },
                new SqlTemplate
                {
                    Abbreviation = "lj",
                    Expansion = "LEFT JOIN $cursor ON "
                },
                new SqlTemplate
                {
                    Abbreviation = "w",
                    Expansion = "WHERE $cursor"
                },
                new SqlTemplate
                {
                    Abbreviation = "ob",
                    Expansion = "ORDER BY $cursor"
                },
                new SqlTemplate
                {
                    Abbreviation = "gb",
                    Expansion = "GROUP BY $cursor"
                },
            };
        }
    }

    /// <summary>
    /// SQL 模板定义。
    /// </summary>
    public class SqlTemplate
    {
        [JsonProperty("abbreviation")]
        public string? Abbreviation { get; set; }

        [JsonProperty("expansion")]
        public string? Expansion { get; set; }
    }
}
