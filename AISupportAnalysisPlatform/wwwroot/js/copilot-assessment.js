(function () {
    function ready(fn) {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', fn);
            return;
        }

        fn();
    }

    ready(function initializeCopilotAssessmentLab() {
        const app = document.getElementById('copilot-assessment-app');
        if (!app) {
            return;
        }

        const runButton = document.getElementById('btnRunAll');
        const statusRate = document.getElementById('statSuccessRate');
        const statusLatency = document.getElementById('statLatency');
        const summary = document.getElementById('assessmentSummary');

        if (!runButton) {
            return;
        }

        const dataset = app.dataset;
        const runUrl = dataset.runUrl || '';
        const runLabel = dataset.runLabel || 'Run Assessment';
        const runningLabel = dataset.runningLabel || 'Running assessment...';
        const passLabel = dataset.passLabel || 'Pass';
        const failLabel = dataset.failLabel || 'Fail';
        const pendingLabel = dataset.pendingLabel || 'Pending';
        const completeTitle = dataset.assessmentCompleteTitle || 'Assessment Complete';
        const errorTitle = dataset.assessmentErrorTitle || 'Assessment Failed';

        runButton.addEventListener('click', async function () {
            if (!runUrl) {
                return;
            }

            setRunningState(true);
            resetRows();

            try {
                const response = await fetch(runUrl, {
                    method: 'POST',
                    headers: {
                        'X-Requested-With': 'XMLHttpRequest'
                    }
                });

                const payload = await response.json();
                if (!response.ok || !payload || payload.success !== true || !payload.data) {
                    throw new Error(payload?.error || payload?.message || 'Assessment request failed.');
                }

                const runSummary = payload.data;
                updateSummary(runSummary);
                updateRows(runSummary.results || []);

                const percent = Number(runSummary.successRate || 0) * 100;
                showDialog('success', completeTitle, `Evaluated ${runSummary.totalCases} cases with ${percent.toFixed(0)}% success.`);
            } catch (error) {
                if (summary) {
                    summary.textContent = error.message || 'Assessment request failed.';
                }

                showDialog('error', errorTitle, error.message || 'Assessment request failed.');
            } finally {
                setRunningState(false);
            }
        });

        function setRunningState(isRunning) {
            runButton.disabled = isRunning;
            runButton.innerHTML = isRunning
                ? `<span class="spinner-border spinner-border-sm ${document.documentElement.dir === 'rtl' ? 'ms-2' : 'me-2'}" role="status" aria-hidden="true"></span><span>${escapeHtml(runningLabel)}</span>`
                : `<i class="bi bi-play-fill ${document.documentElement.dir === 'rtl' ? 'ms-1' : 'me-1'}"></i><span>${escapeHtml(runLabel)}</span>`;

            if (summary && isRunning) {
                summary.textContent = runningLabel;
            }
        }

        function resetRows() {
            document.querySelectorAll('#testResultsBody tr[data-case-id]').forEach(function (row) {
                const status = row.querySelector('.status-col');
                const route = row.querySelector('.actual-route');
                const detail = row.querySelector('.result-detail');
                const latency = row.querySelector('.latency-col');

                if (status) {
                    status.innerHTML = '<span class="spinner-grow spinner-grow-sm text-primary" role="status" aria-hidden="true"></span>';
                }

                if (route) {
                    route.textContent = '---';
                }

                if (detail) {
                    detail.textContent = 'Running scenario...';
                }

                if (latency) {
                    latency.textContent = '---';
                }
            });

            if (statusRate) {
                statusRate.textContent = '-';
            }

            if (statusLatency) {
                statusLatency.textContent = '-';
            }
        }

        function updateSummary(runSummary) {
            const percent = Number(runSummary.successRate || 0) * 100;

            if (statusRate) {
                statusRate.textContent = `${percent.toFixed(0)}%`;
            }

            if (statusLatency) {
                statusLatency.textContent = `${Number(runSummary.averageLatencyMs || 0)}ms`;
            }

            if (summary) {
                summary.textContent = `Run #${runSummary.summaryId} finished with ${runSummary.successCount}/${runSummary.totalCases} passing cases.`;
            }
        }

        function updateRows(results) {
            results.forEach(function (result) {
                const row = document.getElementById(`row-${result.id}`);
                if (!row) {
                    return;
                }

                const status = row.querySelector('.status-col');
                const route = row.querySelector('.actual-route');
                const detail = row.querySelector('.result-detail');
                const latency = row.querySelector('.latency-col');

                if (status) {
                    status.innerHTML = result.isSuccess
                        ? `<span class="badge bg-soft-success text-success rounded-pill">${escapeHtml(passLabel)}</span>`
                        : `<span class="badge bg-soft-danger text-danger rounded-pill">${escapeHtml(failLabel)}</span>`;
                }

                if (route) {
                    const parts = [result.actualMode, result.actualIntent, result.actualTool]
                        .filter(function (value) { return value && value.trim().length > 0 && value !== 'none'; });
                    route.textContent = parts.length ? parts.join(' | ') : pendingLabel;
                }

                if (detail) {
                    const preview = result.answerPreview ? ` ${result.answerPreview}` : '';
                    detail.textContent = `${result.detail || ''}${preview}`.trim() || pendingLabel;
                }

                if (latency) {
                    latency.textContent = `${Number(result.latencyMs || 0)}ms`;
                }
            });
        }

        function showDialog(icon, title, text) {
            if (window.Swal) {
                window.Swal.fire({
                    title: title,
                    text: text,
                    icon: icon,
                    confirmButtonColor: '#3b82f6'
                });
                return;
            }

            window.alert(`${title}\n\n${text}`);
        }

        function escapeHtml(value) {
            return String(value || '')
                .replaceAll('&', '&amp;')
                .replaceAll('<', '&lt;')
                .replaceAll('>', '&gt;')
                .replaceAll('"', '&quot;')
                .replaceAll("'", '&#39;');
        }
    });
})();
