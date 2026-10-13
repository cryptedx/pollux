// Snap Board QR worker: finds a QR code in the Snap's pixels off the page's thread. jsQR 1.4.0 is vendored
// (npm "jsqr", Apache-2.0); it only decodes the pixels it is given.
'use strict';
importScripts('jsqr/jsQR.js');

self.onmessage = e => {
  const { id, width, height, pixels } = e.data;
  let found = null;
  try {
    const code = jsQR(new Uint8ClampedArray(pixels), width, height, { inversionAttempts: 'attemptBoth' });
    if (code && code.data) found = code.data;
  } catch { /* unreadable: no code */ }
  self.postMessage({ id, data: found });
};
