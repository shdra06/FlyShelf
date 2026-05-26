'use strict';

export const dynamic = 'force-static';

export default function robots() {
  return {
    rules: {
      userAgent: '*',
      allow: '/',
      disallow: '/private/',
    },
    sitemap: 'https://shdra06.github.io/FlyShelf/sitemap.xml',
  };
}
