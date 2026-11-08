using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.App.Presentation;

public sealed class QuotaGroupViewModel : ObservableObject
{
    private string _groupTitle = "";
    private bool _hasGroupHeader;

    public QuotaGroupViewModel(string groupTitle, bool hasGroupHeader)
    {
        _groupTitle = groupTitle;
        _hasGroupHeader = hasGroupHeader;
    }

    public string GroupTitle
    {
        get => _groupTitle;
        set => SetField(ref _groupTitle, value);
    }

    public bool HasGroupHeader
    {
        get => _hasGroupHeader;
        set => SetField(ref _hasGroupHeader, value);
    }

    public ObservableCollection<ModelQuotaItemViewModel> Buckets { get; } = [];
}

public sealed class ModelQuotaItemViewModel : ObservableObject
{
    private static readonly Brush RingGreenBrush =
        CreateBrush(0x4E, 0x9A, 0x51);
    private static readonly Brush WarningBrush =
        CreateBrush(0xC6, 0x7A, 0x08);
    private static readonly Brush DangerBrush =
        CreateBrush(0xC2, 0x41, 0x0C);
    private static readonly Brush DeadBrush =
        CreateBrush(0x8C, 0x82, 0x78);

    private static readonly Brush PrimaryTextBrush =
        CreateBrush(0x1E, 0x1B, 0x18);
    private static readonly Brush MutedTextBrush =
        CreateBrush(0x6E, 0x67, 0x5F);

    private string _modelId = "";
    private string _displayName = "";
    private string _shortName = "";
    private string? _detailLabel;
    private int _remainingPercent;
    private string _percentText = "--";
    private string _percentLeftText = "";
    private string? _resetTimeText;
    private string _popupResetText = "";
    private string _summaryText = "";
    private bool _isNested;
    private Brush _titleForeground = PrimaryTextBrush;
    private Brush _percentForeground = MutedTextBrush;
    private Brush _progressBrush = RingGreenBrush;
    private Geometry _ringTrackGeometry = Geometry.Empty;
    private Geometry _ringArcGeometry = Geometry.Empty;
    private bool _isDataAvailable;

    public ModelQuotaItemViewModel()
    {
    }

    public ModelQuotaItemViewModel(
        ModelQuotaWindow window,
        string? overrideTitle = null,
        string? detailLabel = null,
        bool isNested = false)
    {
        Apply(window, overrideTitle, detailLabel, isNested);
    }

    public string ModelId
    {
        get => _modelId;
        private set => SetField(ref _modelId, value);
    }

    public string DisplayName
    {
        get => _displayName;
        private set => SetField(ref _displayName, value);
    }

    public string ShortName
    {
        get => _shortName;
        private set => SetField(ref _shortName, value);
    }

    public string? DetailLabel
    {
        get => _detailLabel;
        private set
        {
            if (SetField(ref _detailLabel, value))
            {
                OnPropertyChanged(nameof(HasSecondRow));
            }
        }
    }

    public int RemainingPercent
    {
        get => _remainingPercent;
        private set => SetField(ref _remainingPercent, value);
    }

    public string PercentText
    {
        get => _percentText;
        private set => SetField(ref _percentText, value);
    }

    public string PercentLeftText
    {
        get => _percentLeftText;
        private set => SetField(ref _percentLeftText, value);
    }

    public string? ResetTimeText
    {
        get => _resetTimeText;
        private set
        {
            if (SetField(ref _resetTimeText, value))
            {
                OnPropertyChanged(nameof(HasSecondRow));
            }
        }
    }

    public bool HasSecondRow =>
        !string.IsNullOrWhiteSpace(DetailLabel) ||
        !string.IsNullOrWhiteSpace(ResetTimeText);

    public string PopupResetText
    {
        get => _popupResetText;
        private set => SetField(ref _popupResetText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public bool IsNested
    {
        get => _isNested;
        private set
        {
            if (SetField(ref _isNested, value))
            {
                TitleForeground = value ? MutedTextBrush : PrimaryTextBrush;
            }
        }
    }

    public Brush TitleForeground
    {
        get => _titleForeground;
        private set => SetField(ref _titleForeground, value);
    }

    public Brush PercentForeground
    {
        get => _percentForeground;
        private set => SetField(ref _percentForeground, value);
    }

    public Brush ProgressBrush
    {
        get => _progressBrush;
        private set => SetField(ref _progressBrush, value);
    }

    public Geometry RingTrackGeometry
    {
        get => _ringTrackGeometry;
        private set => SetField(ref _ringTrackGeometry, value);
    }

    public Geometry RingArcGeometry
    {
        get => _ringArcGeometry;
        private set => SetField(ref _ringArcGeometry, value);
    }

    public bool IsDataAvailable
    {
        get => _isDataAvailable;
        private set => SetField(ref _isDataAvailable, value);
    }

    public void Apply(
        ModelQuotaWindow? window,
        string? overrideTitle = null,
        string? detailLabel = null,
        bool isNested = false)
    {
        if (window is null)
        {
            ModelId = "";
            DisplayName = "";
            ShortName = "대기 중";
            DetailLabel = null;
            RemainingPercent = 0;
            PercentText = "--";
            PercentLeftText = "";
            ResetTimeText = null;
            PopupResetText = "";
            SummaryText = "";
            IsNested = false;
            TitleForeground = MutedTextBrush;
            PercentForeground = MutedTextBrush;
            ProgressBrush = DeadBrush;
            RingTrackGeometry = CreateCircleRingGeometry(1.0);
            RingArcGeometry = Geometry.Empty;
            IsDataAvailable = false;
            return;
        }

        ModelId = window.ModelId;
        var resolvedTitle = !string.IsNullOrWhiteSpace(overrideTitle)
            ? overrideTitle!
            : !string.IsNullOrWhiteSpace(window.Window)
                ? window.WindowLabel
                : window.DisplayName;

        DisplayName = resolvedTitle;
        ShortName = !string.IsNullOrWhiteSpace(window.FamilyGroup)
            ? window.FamilyGroup!
            : FavoriteModelsPolicy.NormalizeFamilyName(window.DisplayName);
        DetailLabel = detailLabel;
        IsNested = isNested;
        TitleForeground = isNested ? MutedTextBrush : PrimaryTextBrush;

        RemainingPercent = window.RemainingPercent;
        PercentText = $"{RemainingPercent}%";
        PercentLeftText = UsageDisplaySettings.FormatPercentLeft(window.RemainingFraction);
        PercentForeground = RemainingPercent >= 100
            ? MutedTextBrush
            : PrimaryTextBrush;

        // Antigravity 원본 로직:
        // - RetrieveUserQuotaSummary 버킷인 경우(Window가 존재하는 경우):
        //   description이 있을 때만 Resets in Xd Yh 표시 (100%일 때는 숨김)
        // - 레거시 fetchAvailableModels 폴백인 경우(Window가 없는 경우):
        //   100% 미만일 때 Resets in Xd Yh 표시
        var effectiveDescription = !string.IsNullOrWhiteSpace(window.Window)
            ? window.Description
            : window.RemainingPercent < 100
                ? "used"
                : null;

        ResetTimeText = UsageDisplaySettings.FormatResetSubtext(
            effectiveDescription,
            window.ResetsAt);
        PopupResetText = ResetTimeText ?? window.WindowLabel;
        SummaryText = string.IsNullOrWhiteSpace(ResetTimeText)
            ? $"{ShortName} · {PercentLeftText}"
            : $"{ShortName} · {PercentLeftText} · {ResetTimeText}";

        IsDataAvailable = true;
        ProgressBrush = window.Disabled
            ? DeadBrush
            : window.DisplayLevel switch
            {
                QuotaDisplayLevel.Danger => DangerBrush,
                QuotaDisplayLevel.Warning => WarningBrush,
                QuotaDisplayLevel.Dead => DeadBrush,
                _ => RingGreenBrush
            };

        RingTrackGeometry = CreateCircleRingGeometry(1.0);
        RingArcGeometry = CreateCircleRingGeometry(window.RemainingFraction);
    }

    /// <summary>
    /// 16x16 뷰박스 기준 중심 (8,8), 반지름 6.2인 원형 게이지(Ring) Geometry를 생성한다.
    /// 12시 방향(0도)에서 시계 방향으로 fraction만큼 호(Arc)를 그린다.
    /// </summary>
    internal static Geometry CreateCircleRingGeometry(double fraction)
    {
        var clamped = Math.Clamp(fraction, 0.0, 1.0);
        if (clamped <= 0.001)
        {
            return Geometry.Empty;
        }

        const double centerX = 8.0;
        const double centerY = 8.0;
        const double radius = 6.2;

        if (clamped >= 0.999)
        {
            var ellipse = new EllipseGeometry(
                new Point(centerX, centerY),
                radius,
                radius);
            ellipse.Freeze();
            return ellipse;
        }

        var angleRadians = clamped * 2.0 * Math.PI;
        var startPoint = new Point(centerX, centerY - radius);
        var endX = centerX + (radius * Math.Sin(angleRadians));
        var endY = centerY - (radius * Math.Cos(angleRadians));
        var endPoint = new Point(endX, endY);
        var isLargeArc = clamped > 0.5;

        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(
            new ArcSegment(
                endPoint,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: isLargeArc,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true));

        var pathGeometry = new PathGeometry([figure]);
        pathGeometry.Freeze();
        return pathGeometry;
    }

    private static Brush CreateBrush(
        byte red,
        byte green,
        byte blue)
    {
        var brush = new SolidColorBrush(
            Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
