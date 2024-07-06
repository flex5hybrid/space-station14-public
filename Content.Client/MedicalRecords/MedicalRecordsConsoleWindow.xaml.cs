using Content.Client.UserInterface.Controls;
using Content.Shared.Access.Systems;
using Content.Shared.Administration;
using Content.Shared.MedicalRecords;
using Content.Shared.Dataset;
using Content.Shared.Medical;
using Content.Shared.StationRecords;
using Robust.Client.AutoGenerated;
using Robust.Client.Player;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Client.MedicalRecords;

// TODO: dedupe shitcode from general records theres a lot
[GenerateTypedNameReferences]
public sealed partial class MedicalRecordsConsoleWindow : FancyWindow
{
    private readonly IPlayerManager _player;
    private readonly IPrototypeManager _proto;
    private readonly IRobustRandom _random;
    private readonly AccessReaderSystem _accessReader;

    public readonly EntityUid Console;

    [ValidatePrototypeId<DatasetPrototype>]
    private const string ReasonPlaceholders = "MedicalRecordsDeadReasonPlaceholders";

    public Action<uint?>? OnKeySelected;
    public Action<StationRecordFilterType, string>? OnFiltersChanged;
    public Action<MedicalStatus>? OnStatusSelected;
    public Action<MedicalRecord, bool, bool>? OnHistoryUpdated;
    public Action? OnHistoryClosed;
    public Action<MedicalStatus, string>? OnDialogConfirmed;

    private uint _maxLength;
    private bool _isPopulating;
    private bool _access;
    private uint? _selectedKey;
    private MedicalRecord? _selectedRecord;

    private DialogWindow? _reasonDialog;

    private StationRecordFilterType _currentFilterType;

    public MedicalRecordsConsoleWindow(EntityUid console, uint maxLength, IPlayerManager playerManager, IPrototypeManager prototypeManager, IRobustRandom robustRandom, AccessReaderSystem accessReader)
    {
        RobustXamlLoader.Load(this);

        Console = console;
        _player = playerManager;
        _proto = prototypeManager;
        _random = robustRandom;
        _accessReader = accessReader;

        _maxLength = maxLength;
        _currentFilterType = StationRecordFilterType.Name;

        OpenCentered();

        // FilterType.Text = StationRecordFilterType.Name;

        foreach (var status in Enum.GetValues<MedicalStatus>())
        {
            AddStatusSelect(status);
        }

        OnClose += () => _reasonDialog?.Close();

        RecordListing.OnItemSelected += args =>
        {
            if (_isPopulating || RecordListing[args.ItemIndex].Metadata is not uint cast)
                return;

            OnKeySelected?.Invoke(cast);
        };

        RecordListing.OnItemDeselected += _ =>
        {
            if (!_isPopulating)
                OnKeySelected?.Invoke(null);
        };


        FilterText.OnTextEntered += args =>
        {
            FilterListingOfRecords(args.Text);
        };

        StatusOptionButton.OnItemSelected += args =>
        {
            SetStatus((MedicalStatus) args.Id);
        };

        HistoryButton.OnPressed += _ =>
        {
            if (_selectedRecord is {} record)
                OnHistoryUpdated?.Invoke(record, _access, true);
        };
    }

    public void UpdateState(MedicalRecordsConsoleState state)
    {
        if (state.Filter != null)
        {
            if (state.Filter.Type != _currentFilterType)
            {
                _currentFilterType = state.Filter.Type;
            }

            if (state.Filter.Value != FilterText.Text)
            {
                FilterText.Text = state.Filter.Value;
            }
        }

        _selectedKey = state.SelectedKey;

        // FilterType.SelectId((int)_currentFilterType);

        // set up the records listing panel
        RecordListing.Clear();

        var hasRecords = state.RecordListing != null && state.RecordListing.Count > 0;
        NoRecords.Visible = !hasRecords;
        if (hasRecords)
            PopulateRecordListing(state.RecordListing!);

        // set up the selected person's record
        var selected = _selectedKey != null;

        PersonContainer.Visible = selected;
        RecordUnselected.Visible = !selected;

        _access = _player.LocalSession?.AttachedEntity is {} player
            && _accessReader.IsAllowed(player, Console);

        // hide access-required editing parts when no access
        var editing = _access && selected;
        StatusOptionButton.Disabled = !editing;

        if (state is { MedicalRecord: not null, StationRecord: not null })
        {
            PopulateRecordContainer(state.StationRecord, state.MedicalRecord);
            OnHistoryUpdated?.Invoke(state.MedicalRecord, _access, false);
            _selectedRecord = state.MedicalRecord;
        }
        else
        {
            _selectedRecord = null;
            OnHistoryClosed?.Invoke();
        }
    }

    private void PopulateRecordListing(Dictionary<uint, string> listing)
    {
        _isPopulating = true;

        foreach (var (key, name) in listing)
        {
            var item = RecordListing.AddItem(name);
            item.Metadata = key;
            item.Selected = key == _selectedKey;
        }
        _isPopulating = false;

        RecordListing.SortItemsByText();
    }

    private void PopulateRecordContainer(GeneralStationRecord stationRecord, MedicalRecord medicalRecord)
    {
        var na = Loc.GetString("generic-not-available-shorthand");
        PersonName.Text = stationRecord.Name;

        StatusOptionButton.SelectId((int) medicalRecord.Status);
        if (medicalRecord.Reason is {} reason)
        {
            var message = FormattedMessage.FromMarkup(Loc.GetString("medical-records-console-dead-reason"));
            message.AddText($": {reason}");
            DeadReason.SetMessage(message);
            DeadReason.Visible = true;
        }
        else
        {
            DeadReason.Visible = false;
        }
    }

    private void AddStatusSelect(MedicalStatus status)
    {
        var name = Loc.GetString($"medical-records-status-{status.ToString().ToLower()}");
        StatusOptionButton.AddItem(name, (int)status);
    }

    private void FilterListingOfRecords(string text = "")
    {
        if (!_isPopulating)
        {
            OnFiltersChanged?.Invoke(StationRecordFilterType.Name, text);
        }
    }

    private void SetStatus(MedicalStatus status)
    {
        if (status == MedicalStatus.Dead || status == MedicalStatus.DeadNonClone || status == MedicalStatus.DeadWithoutSoul)
        {
            GetReason(status);
            return;
        }

        OnStatusSelected?.Invoke(status);
    }

    private void GetReason(MedicalStatus status)
    {
        if (_reasonDialog != null)
        {
            _reasonDialog.MoveToFront();
            return;
        }

        var field = "reason";
        var title = Loc.GetString("medical-records-status-" + status.ToString().ToLower());
        var placeholders = _proto.Index<DatasetPrototype>(ReasonPlaceholders);
        var placeholder = Loc.GetString("medical-records-console-reason-placeholder", ("placeholder", _random.Pick(placeholders.Values))); // just funny it doesn't actually get used
        var prompt = Loc.GetString("medical-records-console-reason");
        var entry = new QuickDialogEntry(field, QuickDialogEntryType.LongText, prompt, placeholder);
        var entries = new List<QuickDialogEntry>() { entry };
        _reasonDialog = new DialogWindow(title, entries);

        _reasonDialog.OnConfirmed += responses =>
        {
            var reason = responses[field];
            if (reason.Length < 1 || reason.Length > _maxLength)
                return;

            OnDialogConfirmed?.Invoke(status, reason);
        };

        _reasonDialog.OnClose += () => { _reasonDialog = null; };
    }
}
