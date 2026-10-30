using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Diagnostics;

namespace FamilyFeud.Components.Shared
{
    /// <summary>
    /// A text input with a dropdown list (#64): the user can type any value or pick one of <see cref="Options"/>.
    /// Use it with <c>@bind-Value</c>. Typing filters the list (case-insensitive "contains"); the chevron button
    /// shows the whole list. ArrowDown/ArrowUp move the highlight, Enter picks it and Escape closes the list.
    /// </summary>
    public partial class ComboBox
    {
        // A form attribute naming no form leaves the input without a form owner (see DetachedFormId)
        private const string NoFormId = "ff-combobox-no-form";

        private readonly string _generatedId = $"ff-combobox-{Guid.NewGuid():N}";
        private ElementReference _input;
        private List<string> _filtered = new();
        private string _filter = string.Empty;
        private bool _isOpen = false;
        private int _highlightedIndex = -1;
        private bool _scrollToHighlighted = false;

        [Inject]
        private IJSRuntime JS { get; set; } = null!;

        [Parameter]
        public string Value { get; set; } = string.Empty;

        [Parameter]
        public EventCallback<string> ValueChanged { get; set; }

        [Parameter]
        public IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();

        /// <summary>The input's id, for a <c>&lt;label for&gt;</c>.</summary>
        [Parameter]
        public string? Id { get; set; }

        [Parameter]
        public string? Placeholder { get; set; }

        /// <summary>Extra classes for the input, such as Bootstrap's <c>form-control</c>.</summary>
        [Parameter]
        public string? InputClass { get; set; }

        private bool IsListShown => _isOpen && _filtered.Count > 0;

        private string InputId => string.IsNullOrEmpty(Id) ? _generatedId : Id;

        private string ListId => $"{InputId}-list";

        private string? ActiveOptionId => IsListShown && _highlightedIndex >= 0 ? OptionId(_highlightedIndex) : null;

        /// <summary>
        /// While an option is highlighted, Enter picks it and must not submit the surrounding form. Blazor's
        /// keydown preventDefault can't be limited to Enter (it would also swallow typing, Tab and Backspace),
        /// so for that time the input is detached from its form instead: an input without a form owner doesn't
        /// submit on Enter. With nothing highlighted the attribute is removed and Enter submits as usual.
        /// </summary>
        private string? DetachedFormId => ActiveOptionId != null ? NoFormId : null;

        protected override void OnParametersSet()
        {
            // The options can arrive after the list was opened (NewQuestion loads them asynchronously)
            if (_isOpen)
            {
                UpdateFiltered();
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!_scrollToHighlighted) return;
            _scrollToHighlighted = false;

            string? optionId = ActiveOptionId;
            if (optionId == null) return;

            try
            {
                // Keep the highlighted option visible in the scrolling list: option.scrollIntoView({ block: "nearest" })
                await using IJSObjectReference option = await JS.InvokeAsync<IJSObjectReference>("document.getElementById", optionId);
                await option.InvokeVoidAsync("scrollIntoView", new { block = "nearest" });
            }
            catch (JSException ex)
            {
                Debug.WriteLine($"Error scrolling the combobox option into view: {ex}");
            }
            catch (JSDisconnectedException)
            {
                // The window is closing
            }
        }

        private string OptionId(int index) => $"{InputId}-option-{index}";

        private async Task OnInputAsync(ChangeEventArgs e)
        {
            string text = e.Value?.ToString() ?? string.Empty;
            Value = text;

            // Typing shows the matching options with nothing highlighted, so Enter still submits the typed value
            _filter = text;
            _highlightedIndex = -1;
            _isOpen = true;
            UpdateFiltered();

            await ValueChanged.InvokeAsync(text);
        }

        private async Task OnKeyDownAsync(KeyboardEventArgs e)
        {
            switch (e.Key)
            {
                case "ArrowDown":
                    MoveHighlight(1);
                    break;

                case "ArrowUp":
                    MoveHighlight(-1);
                    break;

                case "Enter":
                    if (ActiveOptionId != null)
                    {
                        await SelectAsync(_filtered[_highlightedIndex]);
                    }
                    else
                    {
                        CloseList();
                    }
                    break;

                case "Escape":
                    CloseList();
                    break;
            }
        }

        private async Task OnToggleMouseDownAsync(MouseEventArgs e)
        {
            if (e.Button != 0) return;

            if (IsListShown)
            {
                CloseList();
            }
            else
            {
                OpenFullList();
            }

            // The prevented mousedown doesn't focus the input, so focus it here (it may not have had focus yet)
            await _input.FocusAsync();
        }

        private async Task OnOptionMouseDownAsync(MouseEventArgs e, string option)
        {
            if (e.Button != 0) return;
            await SelectAsync(option);
        }

        /// <summary>Shows every option, with the current value highlighted if it's one of them.</summary>
        private void OpenFullList()
        {
            _filter = string.Empty;
            _isOpen = true;
            UpdateFiltered();
            _highlightedIndex = _filtered.FindIndex(o => string.Equals(o, Value, StringComparison.OrdinalIgnoreCase));
            _scrollToHighlighted = true;
        }

        /// <summary>
        /// ArrowDown (step 1) / ArrowUp (step -1): moves the highlight, wrapping round at the ends. A closed list opens
        /// in full with the current value highlighted, or else the first (ArrowDown) or last (ArrowUp) option.
        /// </summary>
        private void MoveHighlight(int step)
        {
            if (!IsListShown)
            {
                OpenFullList();
                if (_highlightedIndex >= 0) return;
            }

            int count = _filtered.Count;
            if (count == 0) return;

            if (_highlightedIndex < 0)
            {
                _highlightedIndex = step > 0 ? 0 : count - 1;
            }
            else
            {
                _highlightedIndex = (_highlightedIndex + step + count) % count;
            }

            _scrollToHighlighted = true;
        }

        private async Task SelectAsync(string option)
        {
            Value = option;
            CloseList();
            await ValueChanged.InvokeAsync(option);
        }

        private void CloseList()
        {
            _isOpen = false;
            _highlightedIndex = -1;
        }

        private void UpdateFiltered()
        {
            _filtered = string.IsNullOrEmpty(_filter)
                ? Options.ToList()
                : Options.Where(o => o.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (_highlightedIndex >= _filtered.Count)
            {
                _highlightedIndex = -1;
            }
        }
    }
}
