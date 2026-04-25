using System.ComponentModel.DataAnnotations;

namespace AISupportAnalysisPlatform.Models
{
    public class Entity
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
    }

    public class TicketCategory
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
    }

    public class TicketPriority
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        public int Level { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public class TicketStatus
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
        public bool IsClosedState { get; set; } = false;
    }

    public class TicketSource
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
    }

    public class SystemSetting
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Key { get; set; } = string.Empty;

        [Required]
        public string Value { get; set; } = string.Empty;
    }

    public class CustomTheme
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(10)]
        public string PrimaryColor { get; set; } = "#10b981";

        [Required]
        [StringLength(10)]
        public string BgMain { get; set; } = "#0a0a0a";

        [Required]
        [StringLength(10)]
        public string BgCard { get; set; } = "#18181b";

        [Required]
        [StringLength(10)]
        public string BgSidebar { get; set; } = "#000000";

        [Required]
        [StringLength(10)]
        public string BgHeader { get; set; } = "#000000";

        [Required]
        [StringLength(10)]
        public string TextMain { get; set; } = "#ffffff";

        [Required]
        [StringLength(10)]
        public string TextMuted { get; set; } = "#d1d5db";

        [Required]
        [StringLength(10)]
        public string BorderColor { get; set; } = "#27272a";

        public bool IsSystemTheme { get; set; } = false;
        public string? SystemIdentifier { get; set; }
    }

    public class ExternalApiSetting
    {
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Endpoint { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public string? Description { get; set; }
    }
}
