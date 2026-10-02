import { api, h, clear, $ } from './core.js';

const status = await api('GET', '/api/auth/status');
if (!status.setupRequired) location.href = status.authenticated ? '/' : '/login.html';

$('#setup-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const msg = $('#msg');
  if ($('#password').value !== $('#password2').value) {
    clear(msg, h('div', { class: 'alert error', text: 'As senhas não conferem.' }));
    return;
  }
  const btn = $('#submit');
  btn.disabled = true;
  try {
    await api('POST', '/api/auth/setup', {
      token: $('#token').value, username: $('#username').value, password: $('#password').value,
    });
    location.href = '/settings.html';
  } catch (err) {
    clear(msg, h('div', { class: 'alert error', text: err.message }));
  } finally {
    btn.disabled = false;
  }
});
