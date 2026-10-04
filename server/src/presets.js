'use strict';

// Quick messages. Clients render their own localized text by `id`;
// `en` is the fallback the server puts in every message.
const PRESETS = {
  together: { en: "Let's do namaz together", ru: 'Давайте совершим намаз вместе', uz: 'Keling, birga namoz o‘qiymiz' },
  coming:   { en: "I'm coming to you to perform namaz", ru: 'Иду к вам совершить намаз', uz: 'Namoz o‘qish uchun sizning oldingizga kelyapman' },
  wait:     { en: 'Wait for me, 5 minutes', ru: 'Подождите меня, 5 минут', uz: 'Meni kuting, 5 daqiqa' },
  where:    { en: 'Where are we praying?', ru: 'Где совершаем намаз?', uz: 'Qayerda namoz o‘qiymiz?' },
  ready:    { en: "I'm ready", ru: 'Я готов', uz: 'Men tayyorman' },
  done:     { en: 'I have already prayed', ru: 'Я уже совершил намаз', uz: 'Men namoz o‘qib bo‘ldim' },
};

module.exports = { PRESETS };
