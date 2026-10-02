import { api, h, clear, $ } from './core.js';

const status = await api('GET', '/api/auth/status');
if (status.setupRequired) location.href = '/setup.html';
else if (status.authenticated) location.href = '/';

$('#login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const btn = $('#submit');
  btn.disabled = true;
  try {
    await api('POST', '/api/auth/login', { username: $('#username').value, password: $('#password').value });
    location.href = '/';
  } catch (err) {
    clear($('#msg'), h('div', { class: 'alert error', text: err.message }));
    $('#password').value = '';
  } finally {
    btn.disabled = false;
  }
});
