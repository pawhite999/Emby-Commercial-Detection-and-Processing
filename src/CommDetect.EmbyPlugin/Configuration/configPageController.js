(function () {
    'use strict';

    var pluginId = '7c4e8f3a-2d51-4b69-9c87-0f3e2a1d5b6c';

    console.log('CommDetect: controller script loaded');

    function initNav(view) {
        var sections = Array.from(view.querySelectorAll('.tab-content'));
        sections.forEach(function (section, i) {
            var nextBtn = section.querySelector('.cd-next');
            var backBtn = section.querySelector('.cd-back');
            if (backBtn) backBtn.style.display = i === 0 ? 'none' : '';
            if (nextBtn) nextBtn.style.display = i === sections.length - 1 ? 'none' : '';
            if (nextBtn) nextBtn.addEventListener('click', function () {
                section.style.display = 'none';
                sections[i + 1].style.display = '';
            });
            if (backBtn) backBtn.addEventListener('click', function () {
                section.style.display = 'none';
                sections[i - 1].style.display = '';
            });
        });
    }

    function loadConfig(view, config) {
        var form = view.querySelector('#CommDetectConfigForm');
        if (!form) return;
        form.querySelectorAll('input, select').forEach(function (field) {
            var name = field.getAttribute('name');
            if (!name || !(name in config)) return;
            if (field.type === 'checkbox') {
                field.checked = config[name] === true || config[name] === 'true';
            } else {
                field.value = (config[name] !== null && config[name] !== undefined) ? config[name] : '';
            }
        });
    }

    function collectConfig(view) {
        var config = {};
        var form = view.querySelector('#CommDetectConfigForm');
        if (!form) return config;
        form.querySelectorAll('input, select').forEach(function (field) {
            var name = field.getAttribute('name');
            if (!name) return;
            if (field.type === 'checkbox') {
                config[name] = field.checked;
            } else if (field.type === 'number') {
                config[name] = field.value === '' ? null : Number(field.value);
            } else {
                config[name] = field.value;
            }
        });
        return config;
    }

    window.CommDetect_run = function () {
        var view = document.getElementById('CommDetectConfigPage');
        if (!view) return;
        console.log('CommDetect: run');

        // Ensure opaque background — Emby doesn't apply it without data-controller
        if (!view._bgSet) {
            view._bgSet = true;
            var rootBg = getComputedStyle(document.documentElement).getPropertyValue('--background-color').trim();
            view.style.backgroundColor = rootBg || '#fff';
        }

        if (!view._cdInit) {
            view._cdInit = true;
            initNav(view);
            var form = view.querySelector('#CommDetectConfigForm');
            if (form) {
                form.addEventListener('submit', function (e) {
                    e.preventDefault();
                    ApiClient.updatePluginConfiguration(pluginId, collectConfig(view)).then(function () {
                        Dashboard.processPluginConfigurationUpdateResult();
                    }).catch(function () {
                        Dashboard.processPluginConfigurationUpdateResult();
                    });
                });
            }
        }

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            console.log('CommDetect: config loaded');
            loadConfig(view, config);
        }).catch(function (err) {
            console.log('CommDetect: config load failed', err);
        });
    };

    // Call immediately — page element is already in DOM when this script loads
    window.CommDetect_run();

    // Handle subsequent visits if Emby re-shows the cached view
    document.addEventListener('viewshow', function (e) {
        if (e.target && e.target.id === 'CommDetectConfigPage') {
            window.CommDetect_run();
        }
    }, true);

})();
