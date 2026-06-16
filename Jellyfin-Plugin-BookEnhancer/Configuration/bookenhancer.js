
        (function () {
            var pluginId = "66bc815f-035b-43a4-82ec-d1f39dfe988b";
            var currentTab = "main";
            var _beLibraries = null;

            function switchTab(tab) {
                currentTab = tab;
                document.querySelectorAll(".tab-content").forEach(function (el) {
                    el.style.display = "none";
                });
                var active = document.getElementById("tab-" + tab);
                if (active) active.style.display = "block";

                document.querySelectorAll(".be-tab").forEach(function (btn) {
                    btn.classList.remove("active");
                    btn.style.color = "var(--text-secondary)";
                    btn.style.borderBottomColor = "transparent";
                });
                var activeBtn = document.querySelector('.be-tab[data-tab="' + tab + '"]');
                if (activeBtn) {
                    activeBtn.classList.add("active");
                    activeBtn.style.color = "var(--accent-color,#00a4dc)";
                    activeBtn.style.borderBottomColor = "var(--accent-color,#00a4dc)";
                }
            }

            document.querySelectorAll(".be-tab").forEach(function (btn) {
                btn.addEventListener("click", function () {
                    switchTab(this.getAttribute("data-tab"));
                });
            });

            switchTab("main");

            var DefaultExtensions = [
                ".epub", ".pdf",
                ".cbz", ".cbr", ".cb7",
                ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
            ];

            var DefaultFormatPriority = [
                { FormatName: "EPUB", Priority: 0 },
                { FormatName: "MOBI", Priority: 1 },
                { FormatName: "PDF", Priority: 2 },
                { FormatName: "Comic", Priority: 3 },
                { FormatName: "Audio", Priority: 4 }
            ];

            function getJellyfinToken() {
                try {
                    if (typeof ApiClient.accessToken === 'function') return ApiClient.accessToken();
                    if (typeof ApiClient.accessToken === 'string') return ApiClient.accessToken;
                    return '';
                } catch (e) { return ''; }
            }

            function authFetch(path, options) {
                options = options || {};
                options.headers = options.headers || {};
                var token = getJellyfinToken();
                if (token) {
                    options.headers['Authorization'] = 'MediaBrowser Token="' + token + '"';
                }
                options.headers['Accept'] = 'application/json';
                var url = window.ApiClient ? window.ApiClient.getUrl(path) : path;
                console.debug("[BookEnhancers] Fetching:", url, "token present:", !!token);
                var timeoutMs = 15000;
                var controller = new AbortController();
                options.signal = controller.signal;
                var timer = setTimeout(function () { controller.abort(); }, timeoutMs);
                return fetch(url, options).then(function (r) {
                    clearTimeout(timer);
                    if (!r.ok) throw new Error('HTTP ' + r.status);
                    return r;
                }, function (err) {
                    clearTimeout(timer);
                    throw err;
                });
            }

            function escapeHtml(str) {
                if (!str) return "";
                return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
            }

            function showTestResult(containerId, success, message) {
                var el = document.getElementById(containerId);
                el.style.display = "block";
                el.className = "be-test-result";
                el.style.color = "var(--text-color)";
                el.style.background = success ? "var(--green-background,rgba(0,200,83,0.15))" : "var(--red-background,rgba(244,67,54,0.15))";
                el.style.border = "1px solid " + (success ? "var(--green,#4caf50)" : "var(--red,#f44336)");
                el.textContent = message;
            }

            function setButtonLoading(btn, loadingText) {
                btn.disabled = true;
                btn.dataset.originalText = btn.textContent;
                btn.textContent = loadingText;
            }

            function resetButton(btn) {
                btn.disabled = false;
                btn.textContent = btn.dataset.originalText || btn.textContent;
            }

            function confirmAction(message) {
                return confirm(message);
            }

            function setFieldInvalid(field, invalid) {
                if (!field) return;
                if (invalid) {
                    field.classList.add("be-invalid");
                } else {
                    field.classList.remove("be-invalid");
                }
            }

            document.getElementById("btnTestHardcover").addEventListener("click", function () {
                var key = document.getElementById("txtHardcoverApiKey").value;
                if (!key) {
                    showTestResult("testHardcoverResult", false, "Enter an API key first.");
                    return;
                }
                var btn = this;
                showTestResult("testHardcoverResult", true, "Testing...");
                setButtonLoading(btn, "Testing...");

                authFetch("Books/Config/TestHardcover", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ ApiKey: key })
                })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        showTestResult("testHardcoverResult", result.success, result.message);
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Hardcover API test failed:", err);
                        showTestResult("testHardcoverResult", false, "Request failed. Check server connectivity.");
                    })
                    .finally(function () {
                        resetButton(btn);
                    });
            });

            document.getElementById("btnTestGoogleBooks").addEventListener("click", function () {
                var key = document.getElementById("txtGoogleBooksApiKey").value;
                var btn = this;
                showTestResult("testGoogleBooksResult", true, "Testing...");
                setButtonLoading(btn, "Testing...");

                authFetch("Books/Config/TestGoogleBooks", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ ApiKey: key })
                })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        showTestResult("testGoogleBooksResult", result.success, result.message);
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Google Books API test failed:", err);
                        showTestResult("testGoogleBooksResult", false, "Request failed. Check server connectivity.");
                    })
                    .finally(function () {
                        resetButton(btn);
                    });
            });

            document.getElementById("btnTestGrandComicsDb").addEventListener("click", function () {
                var username = document.getElementById("txtGrandComicsDbUsername").value;
                var password = document.getElementById("txtGrandComicsDbPassword").value;
                if (!username || !password) {
                    showTestResult("testGrandComicsDbResult", false, "Enter both username and password first.");
                    return;
                }
                var btn = this;
                showTestResult("testGrandComicsDbResult", true, "Testing...");
                setButtonLoading(btn, "Testing...");

                authFetch("Books/Config/TestGrandComicsDb", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ username: username, password: password })
                })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        showTestResult("testGrandComicsDbResult", result.success, result.message);
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] GCD API test failed:", err);
                        showTestResult("testGrandComicsDbResult", false, "Request failed. Check server connectivity.");
                    })
                    .finally(function () {
                        resetButton(btn);
                    });
            });

            document.getElementById("btnTestConnectivity").addEventListener("click", function () {
                var btn = this;
                btn.disabled = true;
                btn.textContent = "Testing...";
                var resultEl = document.getElementById("testConnectivityResult");
                resultEl.style.display = "block";
                resultEl.innerHTML = '<div class="loading-spinner"></div> Checking connectivity...';

                authFetch("Books/Config/TestConnectivity", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" }
                })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        var html = "";
                        var allReachable = true;
                        result.results.forEach(function (svc) {
                            var ok = svc.reachable;
                            if (!ok) allReachable = false;
                            var color = ok ? "var(--green,#4caf50)" : "var(--red,#f44336)";
                            var icon = ok ? "&#x2713;" : "&#x2717;";
                            html += '<div style="padding:4px 0;">';
                            html += '<span style="color:' + color + ';font-weight:600;">' + icon + ' ' + svc.name + '</span>';
                            html += '<span style="color:var(--text-secondary);font-size:11px;margin-left:6px;">' + svc.url + '</span>';
                            if (svc.statusCode) html += '<span style="color:var(--text-secondary);font-size:11px;margin-left:4px;">[' + svc.statusCode + ']</span>';
                            if (svc.error) html += '<div style="color:var(--red,#f44336);font-size:11px;margin-left:16px;">' + escapeHtml(svc.error) + '</div>';
                            html += '</div>';
                        });
                        html += '<div style="margin-top:6px;padding-top:6px;border-top:1px solid var(--border-color);font-weight:600;color:' + (allReachable ? "var(--green,#4caf50)" : "var(--red,#f44336)") + ';">';
                        html += allReachable ? "All services reachable." : "Some services are not reachable — check network/firewall.";
                        html += '</div>';
                        resultEl.innerHTML = html;
                        resultEl.style.background = allReachable ? "var(--green-background,rgba(0,200,83,0.15))" : "var(--red-background,rgba(244,67,54,0.15))";
                        resultEl.style.border = "1px solid " + (allReachable ? "var(--green,#4caf50)" : "var(--red,#f44336)");
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Connectivity test failed:", err);
                        resultEl.innerHTML = '<span style="color:var(--red,#f44336);">Request failed: ' + err.message + '</span>';
                        resultEl.style.background = "var(--red-background,rgba(244,67,54,0.15))";
                        resultEl.style.border = "1px solid var(--red,#f44336)";
                    })
                    .finally(function () {
                        btn.disabled = false;
                        btn.textContent = "Test Enrichment Connectivity";
                    });
            });

            function downloadComicInfoTemplate() {
                var btn = document.getElementById("btnDownloadComicInfoTemplate");
                btn.disabled = true;
                var origText = btn.textContent;
                btn.textContent = "Downloading...";
                var resultEl = document.getElementById("comicInfoTemplateResult");
                resultEl.style.display = "block";
                resultEl.className = "be-test-result";
                resultEl.textContent = "Preparing download...";
                resultEl.style.background = "transparent";
                resultEl.style.border = "1px solid var(--border-color)";

                authFetch("Books/Config/ComicInfoTemplate")
                    .then(function (r) {
                        if (!r.ok) throw new Error("HTTP " + r.status);
                        return r.blob();
                    })
                    .then(function (blob) {
                        var url = window.URL.createObjectURL(blob);
                        var a = document.createElement("a");
                        a.href = url;
                        a.download = "ComicInfoTemplate.xml";
                        document.body.appendChild(a);
                        a.click();
                        document.body.removeChild(a);
                        window.URL.revokeObjectURL(url);
                        resultEl.textContent = "Download started.";
                        resultEl.style.background = "var(--green-background,rgba(0,200,83,0.15))";
                        resultEl.style.border = "1px solid var(--green,#4caf50)";
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Template download failed:", err);
                        resultEl.textContent = "Download failed. Check server logs.";
                        resultEl.style.background = "var(--red-background,rgba(244,67,54,0.15))";
                        resultEl.style.border = "1px solid var(--red,#f44336)";
                    })
                    .finally(function () {
                        btn.disabled = false;
                        btn.textContent = origText;
                    });
            }

            document.getElementById("btnDownloadComicInfoTemplate").addEventListener("click", downloadComicInfoTemplate);

            document.getElementById("btnGenerateComicInfoTemplate").addEventListener("click", function () {
                var btn = this;
                btn.disabled = true;
                var origText = btn.textContent;
                btn.textContent = "Generating...";
                var resultEl = document.getElementById("comicInfoTemplateResult");
                resultEl.style.display = "block";
                resultEl.className = "be-test-result";
                resultEl.textContent = "Generating default template...";
                resultEl.style.background = "transparent";
                resultEl.style.border = "1px solid var(--border-color)";

                authFetch("Books/Config/GenerateComicInfoTemplate", { method: "POST" })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        if (result.success) {
                            resultEl.textContent = result.message + " Path: " + result.path;
                            resultEl.style.background = "var(--green-background,rgba(0,200,83,0.15))";
                            resultEl.style.border = "1px solid var(--green,#4caf50)";
                            updateComicInfoTemplateStatus(result.path, true);
                        } else {
                            resultEl.textContent = result.message || "Failed to generate template.";
                            resultEl.style.background = "var(--red-background,rgba(244,67,54,0.15))";
                            resultEl.style.border = "1px solid var(--red,#f44336)";
                        }
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Template generation failed:", err);
                        resultEl.textContent = "Request failed. Check server logs.";
                        resultEl.style.background = "var(--red-background,rgba(244,67,54,0.15))";
                        resultEl.style.border = "1px solid var(--red,#f44336)";
                    })
                    .finally(function () {
                        btn.disabled = false;
                        btn.textContent = origText;
                    });
            });

            function updateComicInfoTemplateStatus(path, exists) {
                var el = document.getElementById("beComicInfoTemplateStatus");
                if (!el) return;
                if (!path) {
                    el.innerHTML = "Template path is not available.";
                    return;
                }
                el.innerHTML = '<strong>Template path:</strong> <code style="word-break:break-all;">' + escapeHtml(path) + '</code><br>' +
                    (exists
                        ? '<span style="color:var(--green,#4caf50);">&#x2713; Template exists.</span>'
                        : '<span style="color:var(--warning-color,#e8a838);">&#x26A0; Template has not been generated yet.</span>');
            }

            function renderFormatPriority(entries) {
                var container = document.getElementById("beFormatPriorityList");
                container.innerHTML = "";

                if (!entries || entries.length === 0) {
                    container.innerHTML = '<p style="color:var(--text-secondary);font-style:italic;">No format entries configured.</p>';
                    return;
                }

                entries.forEach(function (entry, idx) {
                    var row = document.createElement("div");
                    row.className = "be-priority-row";
                    row.setAttribute("data-idx", idx);
                    row.setAttribute("style", "display:flex;align-items:center;gap:6px;padding:6px 8px;border:1px solid var(--border-color);border-radius:4px;margin-bottom:3px;background:var(--card-background);");

                    var isFirst = idx === 0;
                    var isLast = idx === entries.length - 1;

                    row.innerHTML =
                        '<span class="be-drag-handle" style="cursor:grab;color:var(--text-secondary);font-size:15px;user-select:none;">&#x2630;</span>' +
                        '<span class="be-priority-badge" style="display:inline-block;min-width:22px;text-align:center;background:var(--accent-color,#00a4dc);color:#fff;border-radius:8px;font-size:11px;padding:1px 5px;font-weight:600;">' + (idx + 1) + '</span>' +
                        '<span style="flex:1;font-weight:500;color:var(--text-color);">' + escapeHtml(entry.FormatName) + '</span>' +
                        '<span style="color:var(--text-secondary);font-size:11px;">priority ' + entry.Priority + '</span>' +
                        '<span style="display:flex;gap:2px;">' +
                            '<button is="emby-button" type="button" class="raised be-priority-up" data-idx="' + idx + '" style="font-size:11px;padding:2px 6px;' + (isFirst ? 'opacity:0.3;' : '') + '"' + (isFirst ? ' disabled' : '') + '>&#x25B2;</button>' +
                            '<button is="emby-button" type="button" class="raised be-priority-down" data-idx="' + idx + '" style="font-size:11px;padding:2px 6px;' + (isLast ? 'opacity:0.3;' : '') + '"' + (isLast ? ' disabled' : '') + '>&#x25BC;</button>' +
                        '</span>';

                    container.appendChild(row);
                });

                container.querySelectorAll(".be-priority-up").forEach(function (btn) {
                    btn.addEventListener("click", function () {
                        var idx = parseInt(this.getAttribute("data-idx"));
                        if (idx > 0) {
                            var arr = getCurrentFormatPriority();
                            var tmp = arr[idx - 1];
                            arr[idx - 1] = arr[idx];
                            arr[idx] = tmp;
                            arr = arr.map(function (e, i) { e.Priority = i; return e; });
                            renderFormatPriority(arr);
                        }
                    });
                });

                container.querySelectorAll(".be-priority-down").forEach(function (btn) {
                    btn.addEventListener("click", function () {
                        var idx = parseInt(this.getAttribute("data-idx"));
                        var arr = getCurrentFormatPriority();
                        if (idx < arr.length - 1) {
                            var tmp = arr[idx + 1];
                            arr[idx + 1] = arr[idx];
                            arr[idx] = tmp;
                            arr = arr.map(function (e, i) { e.Priority = i; return e; });
                            renderFormatPriority(arr);
                        }
                    });
                });

                validateFormatPriority();
            }

            function validateFormatPriority() {
                var entries = getCurrentFormatPriority();
                var names = {};
                var hasDupes = false;
                entries.forEach(function (e) {
                    if (names[e.FormatName]) hasDupes = true;
                    names[e.FormatName] = true;
                });
                var warning = document.getElementById("bePriorityWarning");
                if (warning) {
                    warning.style.display = hasDupes ? "block" : "none";
                }
            }

            function getCurrentFormatPriority() {
                var rows = document.querySelectorAll("#beFormatPriorityList .be-priority-row");
                var entries = [];
                rows.forEach(function (row, idx) {
                    var nameEl = row.querySelector("span[style*='flex:1']");
                    if (nameEl) {
                        entries.push({ FormatName: nameEl.textContent, Priority: idx });
                    }
                });
                return entries;
            }

            document.getElementById("btnResetFormatPriority").addEventListener("click", function () {
                renderFormatPriority(JSON.parse(JSON.stringify(DefaultFormatPriority)));
            });

            function renderFileExtensions(selectedExts) {
                var container = document.getElementById("beFileExtList");
                container.innerHTML = "";

                var allExts = DefaultExtensions;
                var set = {};
                if (selectedExts) {
                    selectedExts.forEach(function (e) { set[e.toLowerCase()] = true; });
                }

                allExts.forEach(function (ext) {
                    var label = document.createElement("label");
                    var isChecked = !selectedExts || set[ext.toLowerCase()];
                    label.style.cssText = "display:inline-flex;align-items:center;gap:3px;padding:3px 8px;border:1px solid var(--border-color);border-radius:3px;cursor:pointer;font-size:12px;background:var(--card-background);" + (isChecked ? "" : "opacity:0.55;");
                    label.innerHTML =
                        '<input type="checkbox" class="be-ext-checkbox" value="' + escapeHtml(ext) + '"' + (isChecked ? ' checked' : '') + ' />' +
                        '<span style="color:var(--text-color);">' + escapeHtml(ext) + '</span>';
                    label.querySelector("input").addEventListener("change", function () {
                        label.style.opacity = this.checked ? "1" : "0.55";
                    });
                    container.appendChild(label);
                });
            }

            function getSelectedExtensions() {
                var exts = [];
                document.querySelectorAll(".be-ext-checkbox:checked").forEach(function (cb) {
                    exts.push(cb.value);
                });
                return exts;
            }

            function renderLibraryList(selectedIds) {
                var container = document.getElementById("beLibraryList");
                container.innerHTML = '<p style="color:var(--text-secondary,#888);"><span class="loading-spinner"></span> Loading libraries...</p>';

                authFetch("Books/Libraries")
                    .then(function (r) { return r.json(); })
                    .then(function (libraries) {
                        _beLibraries = libraries;

                        if (!libraries || libraries.length === 0) {
                            container.innerHTML = '<p style="color:var(--text-secondary,#888);">No libraries found.</p>';
                            return;
                        }

                        var html = "";
                        libraries.forEach(function (lib) {
                            var isSelected = !selectedIds || selectedIds.length === 0 || selectedIds.indexOf(lib.id) >= 0;
                            var typeIcon = lib.collectionType === "books" ? "\u{1F4DA}" : "\u{1F3B5}";
                            html += '<label style="display:flex;align-items:center;gap:8px;padding:6px 8px;border:1px solid var(--border-color,#ddd);border-radius:4px;cursor:pointer;margin-bottom:4px;">';
                            html += '<input type="checkbox" class="be-library-checkbox" value="' + escapeHtml(lib.id) + '"' + (isSelected ? ' checked' : '') + ' />';
                            html += '<span>' + typeIcon + ' ' + escapeHtml(lib.name) + '</span>';
                            html += '<span style="color:var(--text-secondary,#888);font-size:12px;">(' + escapeHtml(lib.collectionType) + ')</span>';
                            html += '</label>';
                        });
                        container.innerHTML = html;

                        populateComicLibraryDropdown();

                        if (typeof getCurrentDirectories === 'function') {
                            var dirs = getCurrentDirectories();
                            if (dirs.length > 0) renderDirectories(dirs);
                        }
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Failed to load libraries:", err);
                        container.innerHTML = '<p style="color:var(--text-secondary,#888);">Failed to load libraries.</p>';
                    });
            }

            function getSelectedLibraryIds() {
                var ids = [];
                document.querySelectorAll(".be-library-checkbox:checked").forEach(function (cb) {
                    ids.push(cb.value);
                });
                return ids;
            }

            function renderDirectories(dirs) {
                var container = document.getElementById("managedDirsContainer");
                container.innerHTML = "";

                if (!dirs || dirs.length === 0) {
                    container.innerHTML = '<p style="color:var(--text-secondary);font-style:italic;">No directories configured. Click "Add Directory" to add one.</p>';
                    return;
                }

                var availableLibs = _beLibraries || [];

                var apiSourceOptions = [
                    { key: "Hardcover", label: "Hardcover" },
                    { key: "Google Books", label: "Google Books" },
                    { key: "OpenLibrary", label: "OpenLibrary" },
                    { key: "Comic Vine", label: "Comic Vine" },
                    { key: "Metron", label: "Metron" },
                    { key: "VerseDB", label: "VerseDB" },
                    { key: "Grand Comics Database", label: "Grand Comics DB" }
                ];

                dirs.forEach(function (dir, idx) {
                    var row = document.createElement("div");
                    row.style.cssText = "border:1px solid var(--border-color);border-radius:6px;padding:12px;margin-bottom:8px;background:var(--card-background);";

                    var libOpts = '<option value="">-- Select Library --</option>';
                    var selectedLib = null;
                    availableLibs.forEach(function (lib) {
                        var sel = lib.id === dir.LibraryId ? ' selected' : '';
                        if (sel) selectedLib = lib;
                        libOpts += '<option value="' + escapeHtml(lib.id) + '"' + sel + '>'
                            + escapeHtml(lib.name) + ' (' + escapeHtml(lib.collectionType) + ')</option>';
                    });
                    var libTypeNote = selectedLib
                        ? '<div class="fieldDescription" style="margin-top:4px;">Collection type: <strong>' + escapeHtml(selectedLib.collectionType) + '</strong>. ' +
                          'Items will appear in the corresponding Jellyfin home section.</div>'
                        : '';

                    var enabledApis = dir.EnabledApiSources || [];
                    var apiCheckboxes = apiSourceOptions.map(function (api) {
                        var checked = enabledApis.indexOf(api.key) >= 0 ? ' checked' : '';
                        return '<label style="display:flex;align-items:center;gap:4px;cursor:pointer;white-space:nowrap;">' +
                            '<input is="emby-checkbox" class="dir-api-source" type="checkbox" data-idx="' + idx + '" data-api="' + escapeHtml(api.key) + '"' + checked + ' />' +
                            '<span>' + escapeHtml(api.label) + '</span>' +
                            '</label>';
                    }).join('');

                    row.innerHTML =
                        '<div style="display:flex;align-items:center;gap:12px;margin-bottom:8px;">' +
                            '<label style="display:flex;align-items:center;gap:4px;cursor:pointer;">' +
                                '<input is="emby-checkbox" class="dir-enabled" type="checkbox" data-idx="' + idx + '"' + (dir.Enabled ? ' checked' : '') + ' />' +
                                '<span>Enabled</span>' +
                            '</label>' +
                            '<button is="emby-button" type="button" class="raised btn-remove-dir" data-idx="' + idx + '" style="margin-left:auto;background:#c33;color:#fff;">Remove</button>' +
                        '</div>' +
                        '<div class="inputContainer" style="margin-bottom:4px;">' +
                            '<label class="inputLabel inputLabelUnfocused">Source Path (where files originate)</label>' +
                            '<div style="display:flex;gap:4px;align-items:center;">' +
                                '<input is="emby-input" class="dir-source" type="text" data-idx="' + idx + '" value="' + escapeHtml(dir.SourcePath || '') + '" style="flex:1;" />' +
                                '<button is="emby-button" type="button" class="raised btn-create-dir" data-idx="' + idx + '" style="font-size:11px;padding:2px 8px;">Create</button>' +
                                '<span class="dir-status" data-idx="' + idx + '" style="font-size:11px;white-space:nowrap;"></span>' +
                            '</div>' +
                        '</div>' +
                        '<div class="inputContainer" style="margin-bottom:4px;">' +
                            '<label class="inputLabel inputLabelUnfocused">Target Library</label>' +
                            '<select is="emby-select" class="dir-library-select" data-idx="' + idx + '">' + libOpts + '</select>' +
                            libTypeNote +
                        '</div>' +
                        '<div class="inputContainer" style="margin-bottom:4px;">' +
                            '<label class="inputLabel inputLabelUnfocused">Library Path (auto-filled from library)</label>' +
                            '<input is="emby-input" class="dir-library" type="text" data-idx="' + idx + '" value="' + escapeHtml(dir.LibraryPath || '') + '" />' +
                        '</div>' +
                        '<div class="inputContainer" style="margin-bottom:4px;">' +
                            '<label class="inputLabel inputLabelUnfocused">Organize Template <span style="font-weight:normal;color:var(--text-secondary);font-size:11px;">{Author} {Series} {Title} {BookTitle} {Disc} {Volume} {Publisher}</span></label>' +
                            '<input is="emby-input" class="dir-template" type="text" data-idx="' + idx + '" value="' + escapeHtml(dir.OrganizeTemplate || '') + '" placeholder="e.g. {Author}/{Series}/{Title}" />' +
                        '</div>' +
                        '<div class="be-dir-options">' +
                            '<label class="be-dir-chip" title="Enable title/author online search for this directory">' +
                                '<input is="emby-checkbox" class="dir-title-author-search" type="checkbox" data-idx="' + idx + '"' + (dir.EnableTitleAuthorSearch !== false ? ' checked' : '') + ' />' +
                                '<span>Title/Author Search</span>' +
                            '</label>' +
                            '<label class="be-dir-chip" title="Write enriched metadata back into files">' +
                                '<input is="emby-checkbox" class="dir-metadata-write" type="checkbox" data-idx="' + idx + '"' + (dir.EnableMetadataWriting ? ' checked' : '') + ' />' +
                                '<span>Write Metadata</span>' +
                            '</label>' +
                            '<label class="be-dir-chip" title="Treat PDF and archive files in this directory as comics (parse issue numbers, apply ComicIssue grouping)">' +
                                '<input is="emby-checkbox" class="dir-comic-library" type="checkbox" data-idx="' + idx + '"' + (dir.IsComicLibrary ? ' checked' : '') + ' />' +
                                '<span>Comic Library</span>' +
                            '</label>' +
                            '<label class="be-dir-chip" title="Only removes the per-title folder from the default template. Series books go into {Publisher}/{Series}/file.cbz; standalone books keep {Publisher}/{Title}/file.cbz. Does not affect {Volume} or any other custom template tokens.">' +
                                '<input is="emby-checkbox" class="dir-flat-series" type="checkbox" data-idx="' + idx + '"' + (dir.FlatSeriesStructure ? ' checked' : '') + ' />' +
                                '<span>Flat Series</span>' +
                            '</label>' +
                        '</div>' +
                        '<details style="margin-top:6px;">' +
                            '<summary style="cursor:pointer;font-size:12px;color:var(--text-secondary);text-decoration:underline;">Per-directory API selection (unchecked = use global)</summary>' +
                            '<div style="display:flex;flex-wrap:wrap;gap:4px;margin-top:4px;">' + apiCheckboxes + '</div>' +
                        '</details>';

                    container.appendChild(row);
                });

                container.querySelectorAll(".btn-remove-dir").forEach(function (btn) {
                    btn.addEventListener("click", function () {
                        if (!confirmAction("Remove this managed directory? The source files will not be deleted.")) return;
                        var idx = parseInt(this.getAttribute("data-idx"));
                        var dirs = getCurrentDirectories();
                        dirs.splice(idx, 1);
                        renderDirectories(dirs);
                    });
                });

                container.querySelectorAll(".dir-enabled, .dir-source, .dir-library").forEach(function (input) {
                    input.addEventListener("change", function () { /* live update handled on save */ });
                });

                container.querySelectorAll(".btn-create-dir").forEach(function (btn) {
                    btn.addEventListener("click", function () {
                        var idx = parseInt(this.getAttribute("data-idx"));
                        var dirs = getCurrentDirectories();
                        var dir = dirs[idx];
                        if (!dir || !dir.SourcePath || !dir.SourcePath.trim()) {
                            Dashboard.alert({ title: "No Path", message: "Enter a source path first." });
                            return;
                        }
                        btn.disabled = true;
                        var origText = btn.textContent;
                        btn.textContent = "...";
                        authFetch("Books/Config/ValidateDirectory", {
                            method: "POST",
                            headers: { "Content-Type": "application/json" },
                            body: JSON.stringify({ path: dir.SourcePath, createIfMissing: true })
                        })
                        .then(function (r) { return r.json(); })
                        .then(function (result) {
                            Dashboard.alert({
                                title: result.created ? "Created" : (result.exists ? "Already Exists" : "Failed"),
                                message: result.created ? ("Created:\n" + dir.SourcePath)
                                    : (result.exists ? ("Already exists:\n" + dir.SourcePath)
                                    : ("Could not create:\n" + dir.SourcePath + "\n" + result.message))
                            });
                        })
                        .catch(function (err) {
                            console.error("[BookEnhancers] Failed to create directory:", err);
                            Dashboard.alert({ title: "Error", message: "Failed to create directory." });
                        })
                        .finally(function () {
                            btn.disabled = false;
                            btn.textContent = origText;
                        });
                    });
                });

            function defaultTemplateForCollectionType(collectionType) {
                if (collectionType === "books") return "{Author}/{Series}/{Title}";
                if (collectionType === "music") return "{Author}/{Series}/{Title}";
                return "{Author}/{Series}/{Title}";
            }

            container.querySelectorAll(".dir-library-select").forEach(function (sel) {
                sel.addEventListener("change", function () {
                    var idx = parseInt(this.getAttribute("data-idx"));
                    var libId = this.value;
                    var pathInput = container.querySelector('.dir-library[data-idx="' + idx + '"]');
                    var tmplInput = container.querySelector('.dir-template[data-idx="' + idx + '"]');
                    if (pathInput && libId && _beLibraries) {
                        var lib = _beLibraries.filter(function (l) { return l.id === libId; });
                        if (lib.length > 0 && lib[0].locations && lib[0].locations.length > 0) {
                            pathInput.value = lib[0].locations[0];
                        }
                        if (tmplInput && !tmplInput.value) {
                            tmplInput.value = defaultTemplateForCollectionType(lib.length > 0 ? lib[0].collectionType : "");
                        }
                    }
                });
            });
            }

            function getCurrentDirectories() {
                var dirs = [];
                var container = document.getElementById("managedDirsContainer");

                container.querySelectorAll(".dir-source").forEach(function (input, idx) {
                    var sourceInput = input;
                    var libSelect = container.querySelector('.dir-library-select[data-idx="' + idx + '"]');
                    var libInput = container.querySelector('.dir-library[data-idx="' + idx + '"]');
                    var enabledInput = container.querySelector('.dir-enabled[data-idx="' + idx + '"]');
                    var tmplInput = container.querySelector('.dir-template[data-idx="' + idx + '"]');
                    var titleAuthorInput = container.querySelector('.dir-title-author-search[data-idx="' + idx + '"]');
                    var metadataWriteInput = container.querySelector('.dir-metadata-write[data-idx="' + idx + '"]');
                    var flatSeriesInput = container.querySelector('.dir-flat-series[data-idx="' + idx + '"]');
                    var comicLibraryInput = container.querySelector('.dir-comic-library[data-idx="' + idx + '"]');
                    var apiSourceInputs = container.querySelectorAll('.dir-api-source[data-idx="' + idx + '"]');
                    var enabledApiSources = [];
                    apiSourceInputs.forEach(function (cb) {
                        if (cb.checked) enabledApiSources.push(cb.getAttribute('data-api'));
                    });

                    dirs.push({
                        SourcePath: sourceInput ? sourceInput.value : "",
                        LibraryPath: libInput ? libInput.value : "",
                        Enabled: enabledInput ? enabledInput.checked : true,
                        LibraryId: libSelect ? libSelect.value : "",
                        OrganizeTemplate: tmplInput ? tmplInput.value : "",
                        EnableTitleAuthorSearch: titleAuthorInput ? titleAuthorInput.checked : true,
                        EnableMetadataWriting: metadataWriteInput ? metadataWriteInput.checked : false,
                        FlatSeriesStructure: flatSeriesInput ? flatSeriesInput.checked : false,
                        IsComicLibrary: comicLibraryInput ? comicLibraryInput.checked : false,
                        EnabledApiSources: enabledApiSources
                    });
                });

                return dirs;
            }

            document.getElementById("btnAddDirectory").addEventListener("click", function () {
                var dirs = getCurrentDirectories();
                dirs.push({ SourcePath: "", LibraryPath: "", Enabled: true, LibraryId: "", EnableTitleAuthorSearch: true, EnableMetadataWriting: false, FlatSeriesStructure: false, IsComicLibrary: false, EnabledApiSources: [] });
                renderDirectories(dirs);
            });

            document.getElementById("btnProcessGroups").addEventListener("click", function () {
                if (!confirmAction("Run grouping process? This will modify the plugin grouping database.")) return;
                var btn = this;
                btn.disabled = true;
                btn.textContent = "Processing...";

                authFetch("Books/Grouping/Process", { method: "POST" })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        Dashboard.alert({
                            title: "Grouping Complete",
                            message: "Processed " + result.processedGroups + " groups."
                        });
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Group processing failed:", err);
                        Dashboard.alert({
                            title: "Error",
                            message: "Failed to process groups. Check server logs."
                        });
                    })
                    .finally(function () {
                        btn.disabled = false;
                        btn.textContent = "Process Groups Now";
                    });
            });

            document.getElementById("btnRepairFormatPaths").addEventListener("click", function () {
                if (!confirmAction("Repair format paths? This will cross-reference the grouping database against the Jellyfin library.")) return;
                var btn = this;
                btn.disabled = true;
                btn.textContent = "Repairing...";

                authFetch("Books/Grouping/Repair", { method: "POST" })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        var msg = "Total formats: " + result.totalFormats +
                            "\nFixed (updated JellyfinItemId): " + result.fixed +
                            "\nAlready correct: " + result.skipped +
                            "\nNot found in library: " + result.notFound;
                        if (result.stalePaths && result.stalePaths.length > 0) {
                            msg += "\n\nStale paths (not found in Jellyfin):\n" + result.stalePaths.join("\n");
                        }
                        Dashboard.alert({
                            title: "Repair Complete",
                            message: msg
                        });
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Path repair failed:", err);
                        Dashboard.alert({
                            title: "Error",
                            message: "Repair failed. Check server logs."
                        });
                    })
                    .finally(function () {
                        btn.disabled = false;
                        btn.textContent = "Repair Format Paths";
                    });
            });

            document.getElementById("btnGroupingPreview").addEventListener("click", function () {
                var btn = this;
                btn.disabled = true;
                btn.textContent = "Scanning...";

                authFetch("Books/Grouping/Preview")
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        var parts = [
                            "Strategy: " + result.matchingStrategy,
                            "Files scanned: " + result.totalFiles,
                            "Would be grouped: " + result.groupedCount + " (2+ formats each)",
                            "Would remain ungrouped: " + result.ungroupedCount
                        ];
                        if (result.isPartial) {
                            parts.push("\n(Preview limited to first " + result.groups.length + " candidates. Full scan recommended for complete results.)");
                        }
                        if (result.groups && result.groups.length > 0) {
                            parts.push("\nGroups:");
                            result.groups.forEach(function (g) {
                                parts.push('  "' + (g.title || "?") + '" \u2014 ' + g.formats.length + " formats");
                                g.formats.forEach(function (f) { parts.push("    " + f.formatType + " (" + f.filePath + ")"); });
                            });
                        }
                        Dashboard.alert({
                            title: "Grouping Preview",
                            message: parts.join("\n")
                        });
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Grouping preview failed:", err);
                        Dashboard.alert({
                            title: "Error",
                            message: "Preview failed. Check server logs."
                        });
                    })
                    .finally(function () {
                        btn.disabled = false;
                        btn.textContent = "Grouping Preview";
                    });
            });

            document.getElementById("btnScanAll").addEventListener("click", function () {
                if (!confirmAction("Start ingestion scan now? This will move/copy files from managed source directories into the library.")) return;
                var btn = this;
                btn.disabled = true;
                btn.textContent = "Scanning...";

                authFetch("Books/Ingestion/Scan", { method: "POST" })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        Dashboard.alert({
                            title: "Scan Complete",
                            message: "Scanned " + result.filesFound + " files, added " + result.filesAdded + " new."
                        });
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Ingestion scan failed:", err);
                        Dashboard.alert({
                            title: "Error",
                            message: "Scan failed. Check server logs."
                        });
                    })
                    .finally(function () {
                        btn.disabled = false;
                        btn.textContent = "Scan All Now";
                    });
            });

            function populateComicLibraryDropdown() {
                var sel = document.getElementById("selComicLibrary");
                if (!sel) return;
                sel.innerHTML = '<option value="">-- Auto-detect comic libraries --</option>';
                if (!_beLibraries) return;
                _beLibraries.forEach(function (lib) {
                    var name = (lib.name || "").toLowerCase();
                    var type = (lib.collectionType || "").toLowerCase();
                    var isComic = name.indexOf("comic") >= 0 || type === "comics";
                    var selAttr = isComic ? ' selected' : '';
                    sel.innerHTML += '<option value="' + escapeHtml(lib.id) + '"' + selAttr + '>' +
                        escapeHtml(lib.name) + ' (' + escapeHtml(lib.collectionType) + ')</option>';
                });
            }

            document.getElementById("btnConvertComics").addEventListener("click", function () {
                if (!confirmAction("Convert CBR/CB7 archives to CBZ? Original archives will be moved to the backup/trash directory.")) return;
                var btn = this;
                var resultEl = document.getElementById("convertComicsResult");

                var scanPath = document.getElementById("txtComicScanPath").value.trim();
                if (!scanPath) {
                    var libId = document.getElementById("selComicLibrary").value;
                    if (!libId && _beLibraries) {
                        var comicLib = _beLibraries.filter(function (l) {
                            var name = (l.name || "").toLowerCase();
                            var type = (l.collectionType || "").toLowerCase();
                            return name.indexOf("comic") >= 0 || type === "comics";
                        });
                        if (comicLib.length > 0) {
                            libId = comicLib[0].id;
                        }
                    }
                    if (libId && _beLibraries) {
                        var found = _beLibraries.filter(function (l) { return l.id === libId; });
                        if (found.length > 0 && found[0].locations && found[0].locations.length > 0) {
                            scanPath = found[0].locations[0];
                        }
                    }
                }

                if (!scanPath) {
                    Dashboard.alert({
                        title: "No Path Selected",
                        message: "Select a comic library, enter a custom scan path, or ensure a library with 'Comic' in its name exists."
                    });
                    return;
                }

                btn.disabled = true;
                btn.textContent = "Converting...";
                resultEl.style.display = "block";
                resultEl.innerHTML = '<div class="loading-spinner"></div> Converting comic archives...';
                resultEl.style.background = "transparent";
                resultEl.style.border = "1px solid var(--border-color)";

                authFetch("Books/Config/ConvertCbrToCbz", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ scanPath: scanPath })
                })
                    .then(function (r) { return r.json(); })
                    .then(function (result) {
                        var success = result.errors === 0;
                        var msg = "Found: " + result.filesFound +
                            "\nConverted: " + result.converted +
                            "\nErrors: " + result.errors;
                        if (result.errorDetails && result.errorDetails.length > 0) {
                            msg += "\n\nErrors:\n" + result.errorDetails.join("\n");
                        }
                        resultEl.style.background = success
                            ? "var(--green-background,rgba(0,200,83,0.15))"
                            : "var(--red-background,rgba(244,67,54,0.15))";
                        resultEl.style.border = "1px solid " + (success ? "var(--green,#4caf50)" : "var(--red,#f44336)");
                        resultEl.innerHTML = msg.replace(/\n/g, "<br>");
                    })
                    .catch(function (err) {
                        console.error("[BookEnhancers] Comic conversion failed:", err);
                        resultEl.style.background = "var(--red-background,rgba(244,67,54,0.15))";
                        resultEl.style.border = "1px solid var(--red,#f44336)";
                        resultEl.innerHTML = "Request failed: " + escapeHtml(err.message);
                    })
                    .finally(function () {
                        btn.disabled = false;
                        btn.textContent = "Convert & Tag";
                    });
            });

            document.getElementById("btnResetConfig").addEventListener("click", function () {
                if (!confirm("Reset all plugin settings to defaults? This cannot be undone.")) return;

                ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                    config.UnifiedMetadataEnabled = true;
                    config.ApiRateLimitMaxWaitSeconds = 5;
                    config.HardcoverEnabled = true;
                    config.HardcoverApiKey = "";
                    config.GoogleBooksEnabled = true;
                    config.GoogleBooksApiKey = "";
                    config.OpenLibraryEnabled = true;
                    config.ComicVineEnabled = false;
                    config.ComicVineApiKey = "";
                    config.ComicVineRateLimitPerHour = 180;
                    config.MetronEnabled = false;
                    config.MetronUsername = "";
                    config.MetronPassword = "";
                    config.VerseDbEnabled = false;
                    config.VerseDbApiKey = "";
                    config.GrandComicsDbEnabled = false;
                    config.GrandComicsDbUsername = "";
                    config.GrandComicsDbPassword = "";
                    config.MatchThreshold = 0.85;
                    config.EnableFormatGrouping = true;
                    config.GroupingStrategy = "IsbnOnly";
                    config.FormatPriority = JSON.parse(JSON.stringify(DefaultFormatPriority));
                    config.CopyMode = false;
                    config.IngestionFileExtensions = DefaultExtensions.slice();
                    config.ManagedDirectories = [];
                    config.AutoScanIntervalMinutes = 0;
                    config.IngestionScanTimeoutMinutes = 30;
                    config.LibraryCleanupTimeoutMinutes = 180;
                    config.MetadataEnrichmentTimeoutMinutes = 120;
                    config.IncludedLibraryIds = [];
                    config.TrashDirectory = "";
                    config.TrashCleanupIntervalDays = 7;
                    config.DuplicateReviewTtlDays = 30;
                    config.EnableNonBookDirectoryCleanup = false;
                    config.BackupDirectory = "";
                    config.BackupCleanupIntervalDays = 30;
                    config.EnrichmentCooldownDays = 7;

                    ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                        window.location.reload();
                    });
                });
            });

            function loadConfig() {
                if (window._beLoadingConfig) return;
                window._beLoadingConfig = true;

                ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                    document.getElementById("chkUnifiedMetadata").checked = config.UnifiedMetadataEnabled || false;
                    document.getElementById("txtApiRateLimitMaxWaitSeconds").value = config.ApiRateLimitMaxWaitSeconds ?? 5;
                    document.getElementById("chkHardcoverEnabled").checked = config.HardcoverEnabled || false;
                    document.getElementById("txtHardcoverApiKey").value = config.HardcoverApiKey || "";
                    document.getElementById("chkGoogleBooksEnabled").checked = config.GoogleBooksEnabled || false;
                    document.getElementById("txtGoogleBooksApiKey").value = config.GoogleBooksApiKey || "";
                    document.getElementById("chkOpenLibraryEnabled").checked = config.OpenLibraryEnabled || false;
                    document.getElementById("chkComicVineEnabled").checked = config.ComicVineEnabled || false;
                    document.getElementById("txtComicVineApiKey").value = config.ComicVineApiKey || "";
                    document.getElementById("txtComicVineRateLimitPerHour").value = config.ComicVineRateLimitPerHour ?? 180;
                    document.getElementById("chkMetronEnabled").checked = config.MetronEnabled || false;
                    document.getElementById("txtMetronUsername").value = config.MetronUsername || "";
                    document.getElementById("txtMetronPassword").value = config.MetronPassword || "";
                    document.getElementById("chkVVerseDbEnabled").checked = config.VerseDbEnabled || false;
                    document.getElementById("txtVerseDbApiKey").value = config.VerseDbApiKey || "";
                    document.getElementById("chkGrandComicsDbEnabled").checked = config.GrandComicsDbEnabled || false;
                    document.getElementById("txtGrandComicsDbUsername").value = config.GrandComicsDbUsername || "";
                    document.getElementById("txtGrandComicsDbPassword").value = config.GrandComicsDbPassword || "";
                    document.getElementById("txtMatchThreshold").value = config.MatchThreshold || 0.85;

                    document.getElementById("txtEnrichmentCooldown").value = config.EnrichmentCooldownDays ?? 7;

                    document.getElementById("txtIngestionScanTimeout").value = config.IngestionScanTimeoutMinutes ?? 30;
                    document.getElementById("txtLibraryCleanupTimeout").value = config.LibraryCleanupTimeoutMinutes ?? 180;
                    document.getElementById("txtMetadataEnrichmentTimeout").value = config.MetadataEnrichmentTimeoutMinutes ?? 120;

                    document.getElementById("txtTrashDirectory").value = config.TrashDirectory || "";
                    setFieldInvalid(document.getElementById("txtTrashDirectory"), !config.TrashDirectory);
                    document.getElementById("txtTrashCleanupInterval").value = config.TrashCleanupIntervalDays || 7;
                    document.getElementById("txtDuplicateReviewTtl").value = config.DuplicateReviewTtlDays ?? 30;
                    document.getElementById("chkEnableNonBookDirectoryCleanup").checked = config.EnableNonBookDirectoryCleanup || false;
                    document.getElementById("txtBackupDirectory").value = config.BackupDirectory || "";
                    document.getElementById("txtBackupCleanupInterval").value = config.BackupCleanupIntervalDays || 30;

                    document.getElementById("chkEnableFormatGrouping").checked = config.EnableFormatGrouping !== false;
                    document.getElementById("selGroupingStrategy").value = config.GroupingStrategy || "IsbnOnly";

                    var fp = (config.FormatPriority || DefaultFormatPriority);
                    var seen = {};
                    fp = fp.filter(function (e) {
                        if (seen[e.FormatName]) return false;
                        seen[e.FormatName] = true;
                        return true;
                    });
                    renderFormatPriority(fp);

                    document.getElementById("txtAutoScanInterval").value = config.AutoScanIntervalMinutes || 0;

                    renderDirectories(config.ManagedDirectories || []);
                    renderLibraryList(config.IncludedLibraryIds || []);

                    renderFileExtensions(config.IngestionFileExtensions || DefaultExtensions);

                    if (config.CopyMode) {
                        document.getElementById("radioCopyMode").checked = true;
                    } else {
                        document.getElementById("radioMoveMode").checked = true;
                    }
                }).then(function () {
                    return authFetch("Books/Config/Info")
                        .then(function (r) { return r.json(); })
                        .then(function (info) {
                            document.getElementById("bePluginInfo").innerHTML =
                                '<p><strong>Name:</strong> ' + escapeHtml(info.name) + '</p>' +
                                '<p><strong>Version:</strong> ' + escapeHtml(info.version) + '</p>';
                            updateComicInfoTemplateStatus(info.comicInfoTemplatePath, info.comicInfoTemplateExists);
                        })
                        .catch(function (err) {
                            console.error("[BookEnhancers] Failed to load plugin info:", err);
                            document.getElementById("bePluginInfo").innerHTML = '<p>Could not load plugin info.</p>';
                        });
                }).catch(function (err) {
                    console.error("[BookEnhancers] Failed to load plugin configuration:", err);
                }).finally(function () {
                    window._beLoadingConfig = false;
                });
            }

            var _beLibraries = [];

            document.getElementById("pluginConfigForm").addEventListener("submit", function (e) {
                e.preventDefault();

                var priorityEntries = getCurrentFormatPriority();
                var seen = {};
                var hasDupes = false;
                priorityEntries.forEach(function (e) {
                    if (seen[e.FormatName]) hasDupes = true;
                    seen[e.FormatName] = true;
                });
                if (hasDupes) {
                    Dashboard.alert({
                        title: "Duplicate Format Priority Entries",
                        message: "The format priority list contains duplicate entries. Click 'Reset to Defaults' to fix, then save again."
                    });
                    return;
                }

                var dirs = getCurrentDirectories();
                var container = document.getElementById("managedDirsContainer");
                var hasMissing = false;

                function clearStatuses() {
                    container.querySelectorAll(".dir-status").forEach(function (s) { s.textContent = ""; s.style.color = ""; });
                }

                function checkAllPaths() {
                    clearStatuses();
                    var dirs = getCurrentDirectories();
                    var entries = [];
                    dirs.forEach(function (d, idx) {
                        if (d.Enabled && d.SourcePath && d.SourcePath.trim() !== "")
                            entries.push({ idx: idx, path: d.SourcePath });
                    });

                    if (entries.length === 0) {
                        doSave();
                        return;
                    }

                    Promise.all(entries.map(function (entry) {
                        var statusEl = container.querySelector('.dir-status[data-idx="' + entry.idx + '"]');
                        return authFetch("Books/Config/ValidateDirectory", {
                            method: "POST",
                            headers: { "Content-Type": "application/json" },
                            body: JSON.stringify({ path: entry.path, createIfMissing: false })
                        })
                        .then(function (r) { return r.json(); })
                        .then(function (result) {
                            if (!result.exists) {
                                hasMissing = true;
                                if (statusEl) {
                                    statusEl.textContent = "Not found";
                                    statusEl.style.color = "var(--warning-color,#e8a838)";
                                }
                            } else if (statusEl) {
                                statusEl.textContent = "OK";
                                statusEl.style.color = "var(--green,#4caf50)";
                            }
                        })
                        .catch(function () {
                            if (statusEl) {
                                statusEl.textContent = "Check failed";
                                statusEl.style.color = "var(--text-secondary,#888)";
                            }
                        });
                    })).then(function () {
                        if (hasMissing) {
                            Dashboard.alert({
                                title: "Missing Source Directories",
                                message: "Some source paths don't exist. Use each row's Create button to create them."
                            });
                            return;
                        }
                        doSave();
                    });
                }

                function doSave() {
                    var trashDir = document.getElementById("txtTrashDirectory").value.trim();
                    setFieldInvalid(document.getElementById("txtTrashDirectory"), !trashDir);
                    if (!trashDir) {
                        Dashboard.alert({
                            title: "Trash Directory Required",
                            message: "You must configure a Trash Directory before saving. All plugin tasks are disabled until a trash directory is set."
                        });
                        return;
                    }

                    ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                        config.UnifiedMetadataEnabled = document.getElementById("chkUnifiedMetadata").checked;
                        config.ApiRateLimitMaxWaitSeconds = parseInt(document.getElementById("txtApiRateLimitMaxWaitSeconds").value, 10) || 5;
                        config.HardcoverEnabled = document.getElementById("chkHardcoverEnabled").checked;
                        config.HardcoverApiKey = document.getElementById("txtHardcoverApiKey").value;
                        config.GoogleBooksEnabled = document.getElementById("chkGoogleBooksEnabled").checked;
                        config.GoogleBooksApiKey = document.getElementById("txtGoogleBooksApiKey").value;
                        config.OpenLibraryEnabled = document.getElementById("chkOpenLibraryEnabled").checked;
                        config.ComicVineEnabled = document.getElementById("chkComicVineEnabled").checked;
                        config.ComicVineApiKey = document.getElementById("txtComicVineApiKey").value;
                        config.ComicVineRateLimitPerHour = parseInt(document.getElementById("txtComicVineRateLimitPerHour").value, 10) || 180;
                        config.MetronEnabled = document.getElementById("chkMetronEnabled").checked;
                        config.MetronUsername = document.getElementById("txtMetronUsername").value;
                        config.MetronPassword = document.getElementById("txtMetronPassword").value;
                        config.VerseDbEnabled = document.getElementById("chkVVerseDbEnabled").checked;
                        config.VerseDbApiKey = document.getElementById("txtVerseDbApiKey").value;
                        config.GrandComicsDbEnabled = document.getElementById("chkGrandComicsDbEnabled").checked;
                        config.GrandComicsDbUsername = document.getElementById("txtGrandComicsDbUsername").value;
                        config.GrandComicsDbPassword = document.getElementById("txtGrandComicsDbPassword").value;
                        config.MatchThreshold = parseFloat(document.getElementById("txtMatchThreshold").value) || 0.85;

                        var cooldownValue = parseInt(document.getElementById("txtEnrichmentCooldown").value);
                        config.EnrichmentCooldownDays = isNaN(cooldownValue) ? 7 : cooldownValue;

                        config.TrashDirectory = document.getElementById("txtTrashDirectory").value.trim();
                        config.TrashCleanupIntervalDays = parseInt(document.getElementById("txtTrashCleanupInterval").value) || 0;
                        var duplicateReviewTtlValue = parseInt(document.getElementById("txtDuplicateReviewTtl").value);
                        config.DuplicateReviewTtlDays = isNaN(duplicateReviewTtlValue) ? 30 : duplicateReviewTtlValue;
                        config.EnableNonBookDirectoryCleanup = document.getElementById("chkEnableNonBookDirectoryCleanup").checked;
                        config.BackupDirectory = document.getElementById("txtBackupDirectory").value.trim();
                        config.BackupCleanupIntervalDays = parseInt(document.getElementById("txtBackupCleanupInterval").value) || 0;

                        config.EnableFormatGrouping = document.getElementById("chkEnableFormatGrouping").checked;
                        config.GroupingStrategy = document.getElementById("selGroupingStrategy").value;

                        config.FormatPriority = getCurrentFormatPriority();

                        config.CopyMode = document.getElementById("radioCopyMode").checked;

                        var exts = getSelectedExtensions();
                        config.IngestionFileExtensions = exts.length > 0 ? exts : DefaultExtensions.slice();

                        config.AutoScanIntervalMinutes = parseInt(document.getElementById("txtAutoScanInterval").value) || 0;
                        config.IngestionScanTimeoutMinutes = parseInt(document.getElementById("txtIngestionScanTimeout").value) || 0;
                        config.LibraryCleanupTimeoutMinutes = parseInt(document.getElementById("txtLibraryCleanupTimeout").value) || 0;
                        config.MetadataEnrichmentTimeoutMinutes = parseInt(document.getElementById("txtMetadataEnrichmentTimeout").value) || 0;
                        config.ManagedDirectories = getCurrentDirectories();
                        config.IncludedLibraryIds = getSelectedLibraryIds();

                        ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                            Dashboard.processPluginConfigurationUpdateResult(result);
                        }).catch(function (err) {
                            console.error("[BookEnhancers] Failed to save configuration:", err);
                            Dashboard.alert({ title: "Error", message: "Failed to save configuration." });
                        });
                    }).catch(function (err) {
                        console.error("[BookEnhancers] Failed to read configuration for save:", err);
                    });
                }

                checkAllPaths();
            });

            document.getElementById("btnOpenGuide").addEventListener("click", function () {
                document.getElementById("guideModal").classList.add("open");
            });
            document.getElementById("btnCloseGuide").addEventListener("click", function () {
                document.getElementById("guideModal").classList.remove("open");
            });
            document.getElementById("guideModal").addEventListener("click", function (e) {
                if (e.target === this) this.classList.remove("open");
            });

            function formatBytes(bytes) {
                if (bytes === 0 || !bytes) return "0 B";
                var k = 1024;
                var sizes = ["B", "KB", "MB", "GB", "TB"];
                var i = Math.floor(Math.log(bytes) / Math.log(k));
                return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + " " + sizes[i];
            }

            function formatDate(isoString) {
                if (!isoString) return "-";
                var d = new Date(isoString);
                if (isNaN(d.getTime())) return isoString;
                return d.toLocaleString();
            }

            function renderDuplicateReviews() {
                var loading = document.getElementById("duplicateReviewsLoading");
                var empty = document.getElementById("duplicateReviewsEmpty");
                var error = document.getElementById("duplicateReviewsError");
                var content = document.getElementById("duplicateReviewsContent");
                var body = document.getElementById("duplicateReviewsBody");

                loading.style.display = "block";
                empty.style.display = "none";
                error.style.display = "none";
                content.style.display = "none";
                body.innerHTML = "";

                authFetch("Books/Config/DuplicateReviews")
                    .then(function (r) {
                        if (!r.ok) throw new Error("HTTP " + r.status);
                        return r.json();
                    })
                    .then(function (entries) {
                        loading.style.display = "none";
                        if (!entries || entries.length === 0) {
                            empty.style.display = "block";
                            return;
                        }

                        entries.forEach(function (entry) {
                            var row = document.createElement("tr");
                            row.innerHTML =
                                "<td style='padding:6px;vertical-align:top;word-break:break-all;'>" + escapeHtml(entry.sourcePath) + "</td>" +
                                "<td style='padding:6px;vertical-align:top;word-break:break-all;'>" + escapeHtml(entry.targetPath) + "</td>" +
                                "<td style='padding:6px;text-align:right;white-space:nowrap;'>" + escapeHtml(formatBytes(entry.sourceSize)) + "</td>" +
                                "<td style='padding:6px;text-align:right;white-space:nowrap;'>" + escapeHtml(formatBytes(entry.targetSize)) + "</td>" +
                                "<td style='padding:6px;white-space:nowrap;'>" + escapeHtml(formatDate(entry.firstSeen)) + "</td>" +
                                "<td style='padding:6px;white-space:nowrap;'>" + escapeHtml(formatDate(entry.lastSeen)) + "</td>";
                            body.appendChild(row);
                        });

                        content.style.display = "block";
                    })
                    .catch(function (err) {
                        loading.style.display = "none";
                        error.style.display = "block";
                        error.textContent = "Failed to load duplicate-review records: " + (err.message || err);
                        console.error("[BookEnhancers] Failed to load duplicate reviews:", err);
                    });
            }

            document.getElementById("btnOpenDuplicateReviews").addEventListener("click", function () {
                document.getElementById("duplicateReviewsModal").classList.add("open");
                renderDuplicateReviews();
            });
            document.getElementById("btnCloseDuplicateReviews").addEventListener("click", function () {
                document.getElementById("duplicateReviewsModal").classList.remove("open");
            });
            document.getElementById("duplicateReviewsModal").addEventListener("click", function (e) {
                if (e.target === this) this.classList.remove("open");
            });

            document.getElementById("txtTrashDirectory").addEventListener("input", function () {
                setFieldInvalid(this, !this.value.trim());
            });

            if (!window._beListenersBound) {
                window._beListenersBound = true;
                var pageEl = document.getElementById("booksConfigPage");
                if (pageEl) {
                    pageEl.addEventListener("pageshow", function () { loadConfig(); });
                }
                document.addEventListener("pageshow", function () { if (document.getElementById("booksConfigPage")) loadConfig(); });
            }

            document.addEventListener("viewshow", function onView() {
                var p = document.getElementById("booksConfigPage");
                if (!p) return;
                document.removeEventListener("viewshow", onView);
                loadConfig();
            });

            loadConfig();
        })();