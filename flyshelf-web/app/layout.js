'use strict';

import './globals.css';
import Navbar from './components/Navbar';
import Footer from './components/Footer';

export const metadata = {
  metadataBase: new URL('https://shdra06.github.io/FlyShelf'),
  title: {
    default: 'FlyShelf — Best Cross-Platform Clipboard Manager for Windows & Android',
    template: '%s | FlyShelf Clipboard Sync'
  },
  description: 'Unify your workspace with FlyShelf. Sync text, high-res images, screenshots, and heavy files across all your Windows PCs & Android devices instantly via LAN or cloud. Free & Open-source.',
  keywords: [
    'clipboard manager',
    'clipboard manager for pc',
    'clipboard sync pc to android',
    'best clipboard manager windows',
    'cross platform copy paste',
    'android clipboard overlay',
    'lan clipboard file share',
    'open source clipboard manager',
    'flyshelf clipboard',
    'ditto alternative windows 11'
  ],
  authors: [{ name: 'Shivendra', url: 'https://github.com/shdra06' }],
  creator: 'Shivendra',
  publisher: 'FlyShelf',
  robots: {
    index: true,
    follow: true,
    googleBot: {
      index: true,
      follow: true,
      'max-video-preview': -1,
      'max-image-preview': 'large',
      'max-snippet': -1,
    },
  },
  openGraph: {
    type: 'website',
    locale: 'en_US',
    url: 'https://shdra06.github.io/FlyShelf',
    title: 'FlyShelf — Premium Cross-Device Clipboard & File Sync Ecosystem',
    description: 'Sync your clipboard history, raw files, and screenshots between Windows and Android in real-time. Beautiful glassmorphic UI, zero cloud data stored.',
    siteName: 'FlyShelf',
    images: [
      {
        url: 'https://shdra06.github.io/FlyShelf/og-image.png',
        width: 1200,
        height: 630,
        alt: 'FlyShelf Universal Clipboard & Sync',
      },
    ],
  },
  twitter: {
    card: 'summary_large_image',
    title: 'FlyShelf — Premium Cross-Device Clipboard Sync',
    description: 'Copy on PC, paste on Android instantly. Physics-enabled mobile floating bubble, secure local LAN and cloud sync lanes. Free & open-source.',
    images: ['https://shdra06.github.io/FlyShelf/og-image.png'],
    creator: '@shdra06',
  },
  alternates: {
    canonical: 'https://shdra06.github.io/FlyShelf',
  }
};

export default function RootLayout({ children }) {
  const jsonLd = {
    '@context': 'https://schema.org',
    '@graph': [
      {
        '@type': 'SoftwareApplication',
        '@id': 'https://shdra06.github.io/FlyShelf/#software',
        'name': 'FlyShelf',
        'operatingSystem': 'Windows 10, Windows 11, Android',
        'applicationCategory': 'UtilityApplication',
        'offers': {
          '@type': 'Offer',
          'price': '0.00',
          'priceCurrency': 'USD'
        },
        'downloadUrl': 'https://github.com/shdra06/FlyShelf/releases',
        'featureList': [
          'Universal Clipboard Synchronization',
          'Peer-to-Peer Secure Local File Sharing',
          'Android Native Foreground Background Service & Physics Overlay Ball',
          'Glassmorphic Mica-enabled sumonable desktop dashboard',
          'Gemini Pro Vision AI OCR Text Extraction',
          'Integrated Bulk PDF Merger & Image Converter'
        ],
        'releaseNotes': 'https://github.com/shdra06/FlyShelf/releases/tag/v7.0.0',
        'softwareVersion': '7.0.0',
        'author': {
          '@type': 'Person',
          'name': 'Shivendra',
          'url': 'https://github.com/shdra06'
        }
      },
      {
        '@type': 'Organization',
        '@id': 'https://shdra06.github.io/FlyShelf/#organization',
        'name': 'FlyShelf Open Source Projects',
        'url': 'https://shdra06.github.io/FlyShelf',
        'logo': 'https://shdra06.github.io/FlyShelf/favicon.ico',
        'sameAs': [
          'https://github.com/shdra06/FlyShelf'
        ]
      }
    ]
  };

  return (
    <html lang="en">
      <head>
        <script
          type="application/ld+json"
          dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd).replace(/</g, '\\u003c') }}
        />
      </head>
      <body>
        <Navbar />
        <main style={{ minHeight: 'calc(100vh - 180px)', paddingTop: '80px' }}>
          {children}
        </main>
        <Footer />
      </body>
    </html>
  );
}
