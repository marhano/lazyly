// Shared by every page that shows a release-notes-capable builds list: the landing page's
// "Recent builds" preview (plain HTML, no DataTable), and /Builds + /Project/{name} (full
// DataTable + project filter). Split into two independent pieces so the landing page's simple
// preview can wire up just the modal without pulling in DataTables at all.
var PublishToolBuildsTable = (function ($) {
    function initReleaseNotesModal(releaseNotesData) {
        var $overlay = $('#releaseNotesModal');
        var $title = $('#releaseNotesModalTitle');
        var $content = $('#releaseNotesModalContent');
        var $download = $('#releaseNotesModalDownload');

        function openModal(key) {
            var entry = releaseNotesData[key];
            if (!entry) {
                return;
            }

            $title.text(entry.Title);
            $content.text(entry.Text);
            $download.attr('href', '/download?path=' + encodeURIComponent(key));
            $overlay.removeAttr('hidden');
        }

        function closeModal() {
            $overlay.attr('hidden', true);
        }

        // Delegated on the document (not any one container) so this keeps working regardless of
        // which page/section the "Release Notes" buttons live in, including across DataTables'
        // pagination, which swaps tbody rows in and out of the DOM.
        $(document).on('click', '.pt-view-notes', function () {
            openModal($(this).data('notes-key'));
        });

        $('#releaseNotesModalClose').on('click', closeModal);
        $overlay.on('click', function (e) {
            if (e.target === this) {
                closeModal();
            }
        });
        $(document).on('keydown', function (e) {
            if (e.key === 'Escape') {
                closeModal();
            }
        });
    }

    // Only relevant on pages with the full #buildsTable + optional #projectFilter (i.e. /Builds
    // and /Project/{name}) -- a no-op if #buildsTable isn't on the page.
    function initDataTable() {
        var $table = $('#buildsTable');
        if ($table.length === 0) {
            return;
        }

        var buildsTable = $table.DataTable({
            order: [[2, 'desc']],
            pageLength: 25,
            lengthMenu: [10, 25, 50, 100],
            columnDefs: [{ orderable: false, targets: 5 }],
            language: {
                search: 'Search:',
                info: 'Showing _START_-_END_ of _TOTAL_ builds',
                infoEmpty: 'No builds to show',
                infoFiltered: '(filtered from _MAX_ total)',
                emptyTable: 'No builds match your search.',
                lengthMenu: 'Show _MENU_ per page',
            },
        });

        // Exact-match filter on the Project column, separate from the free-text search box -- a
        // plain column().search() would treat the project name as a regex/substring pattern, which
        // breaks for names containing regex-special characters (parentheses, dots, etc.) and would
        // also match "OmniPay" against a project literally named "OmniPay Business".
        $('#projectFilter').on('change', function () {
            var selected = this.value;
            buildsTable.column(0).search(selected ? '^' + $.fn.dataTable.util.escapeRegex(selected) + '$' : '', true, false).draw();
        });
    }

    function init(releaseNotesData) {
        $(function () {
            initReleaseNotesModal(releaseNotesData || {});
            initDataTable();
        });
    }

    return { init: init };
})(jQuery);
