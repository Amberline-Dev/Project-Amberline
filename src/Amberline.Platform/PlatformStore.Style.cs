using System.Globalization;
using System.Net;

namespace Amberline.Platform;

public static partial class PlatformStore
{
    /// <summary>
    /// The shared store page: markup frame and style sheet.
    /// Exact (measured on the five 25 September 2011 screenshots at 1290x806, the AIR frame is 1262x670 at x=12, y=82):
    /// navigation rail 135x50 tabs at a 51 px pitch with the active tab 52 px high and 2 px right; one framed panel at
    /// x=160..1252, y=91..748 (1 px #2e3a45 border); filter blocks 150 px wide with 128x55 header art; product cards 160x215 at
    /// a 172 x 227 pitch with a 156x210 art, a title over its top, a 53 px translucent footer with the 94x22 Unlock button
    /// and the price line; the home's 780x350 promo, 780x276 "What's New", 270 px "Featured Item" (142 high) and "Top
    /// Sellers" (487 high) columns. The art is the shipped stock art served from /store/img (the stock parent frame maps /img
    /// to app:/assets/storeImages).
    /// Fonts: ITC Friz Quadrata for the rail tabs, the filter-block labels, the panel headers and the promo title and pager digits,
    /// Arial for everything else (measured on the 25 September 2011 screenshots). Friz is never served by the Platform: each PC's companion extracts it
    /// from that PC's own client (css/fonts.swf) and serves it on <see cref="FrizFaceCss"/>'s loopback URL; without it the Arial fallback shows.
    /// Reconstruction: colours between
    /// the measured pixels, and every state the screenshots do not show (hover, disabled, sale, badge placement).
    /// </summary>
    /// <summary>
    /// ITC Friz Quadrata with the Arial-family fallback. The font itself is never in this repository's output: the AIR companion on
    /// each PC converts it from that PC's own client (css/fonts.swf) and serves it on loopback port 8396 (<c>FrizFontServer.Port</c> in
    /// Amberline.AirCompanion; a check pins the two). NOT RETAIL: retail pages loaded Riot's own friz.ttf from the web; a browser that has
    /// no such local server (or an old runtime without @font-face) just shows the fallback.
    /// </summary>
    public const string FrizStack = "'ITC Friz Quadrata',Arial,Helvetica,sans-serif";
    public const string FrizFaceCss =
        "@font-face{font-family:'ITC Friz Quadrata';font-weight:normal;font-style:normal;src:url('http://127.0.0.1:8396/fonts/friz.ttf')}" +
        "@font-face{font-family:'ITC Friz Quadrata';font-weight:bold;font-style:normal;src:url('http://127.0.0.1:8396/fonts/friz-bold.ttf')}";

    private static string Layout(string title, string body, string bodyAttributes = "") => $$"""
        <!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content="width=device-width">
        <title>Amberline store · {{WebUtility.HtmlEncode(title)}}</title>
        <style>
        html{height:100%;background:#000}body{margin:0;padding:0;height:100%;background:#000;color:#ddd6c7;font:12px Arial,sans-serif;overflow:hidden}
        main,section,header,nav,aside,article{display:block}main{position:absolute;top:9px;right:21px;bottom:3px;left:12px;min-width:960px}
        a{color:#e4b46d;text-decoration:none}a:hover{text-decoration:underline}a:focus,button:focus,input:focus,select:focus{outline:1px solid #e2b45f}.clear{clear:both}
        {{FrizFaceCss}}h1,h2{font-family:{{FrizStack}}}img{border:0}
        .store-frame{position:absolute;top:0;right:0;bottom:0;left:0}
        /* navigation rail: navigation_tab.png is five 135x50 states (active, normal, hover, gold, gold hover) */
        .store-nav{position:absolute;top:3px;left:0;width:137px}.store-tabs{display:block}
        .store-tab{display:block;width:123px;height:50px;margin:0 0 1px;padding:0 0 0 12px;background:#0d1826 url('/store/img/layout/navigation_tab.png') 0 -50px no-repeat;color:#becedc;font:14px/50px {{FrizStack}}}
        .store-tab.active{height:52px;margin:0 0 1px 2px;line-height:52px;background-position:0 0;color:#fff}.store-tab:hover{background-position:0 -100px;text-decoration:none;color:#fff}
        .store-rp-link{display:block;width:123px;height:50px;margin:0 0 1px;padding:0 0 0 12px;background:#563916 url('/store/img/layout/navigation_tab.png') 0 -150px no-repeat;color:#f0d9b1;font:14px/50px {{FrizStack}}}
        .store-rp-link:hover,.store-rp-link.active{background-position:0 -200px;text-decoration:none;color:#fff}
        /* the framed panel */
        .store-panel{position:absolute;top:0;right:0;bottom:0;left:136px;border:1px solid #2e3a45;background:#08101a url('/store/img/layout/home_bg.jpg') 0 0 no-repeat;-webkit-border-radius:3px;border-radius:3px;overflow:hidden}
        .store-page{overflow-y:auto}
        /* One dark scroller everywhere (the real client draws a 16 px light-grey Windows bar on every plain overflow area) */
        ::-webkit-scrollbar{width:10px;height:10px;background:#08101a}::-webkit-scrollbar-track{background:#0a1320;border:1px solid #1a2a3e}::-webkit-scrollbar-thumb{background:#1a2f48;border:1px solid #2c4a6b;-webkit-border-radius:4px}::-webkit-scrollbar-corner{background:#08101a}
        /* filter blocks */
        .filter-panel{position:absolute;top:8px;left:5px;width:150px}
        .filter-block{width:148px;margin:0 0 5px;padding:0 0 9px;background:#05070b;border:1px solid #28445f}
        .search-submit,.filter-button,.sort-button{display:block;width:128px;height:55px;margin:0 auto;padding:0;border:0;background:#0b1929 url('/store/img/layout/searchbutton_off.jpg') 0 0 no-repeat;color:#fff;font:12px {{FrizStack}};text-align:center;cursor:pointer;-webkit-border-radius:0;border-radius:0}
        .search-submit span,.filter-button span,.sort-button span{display:block;padding-top:35px}
        .search-submit:hover{background-image:url('/store/img/layout/searchbutton_on.jpg')}.filter-button{background-image:url('/store/img/layout/filterbutton_off.jpg')}.filter-button:hover{background-image:url('/store/img/layout/filterbutton_on.jpg')}.sort-button{background-image:url('/store/img/layout/sortbutton_off.jpg')}
        .search-box{display:block;width:114px;height:14px;margin:0 auto;padding:4px 5px;background:#000;border:1px solid #1f2d3f;color:#e5e5e5;font:12px Arial,sans-serif}.clear-search{display:block;margin:5px 0 0 12px;color:#9fc3da;font-size:10px}
        .check-label{display:block;height:19px;line-height:19px;margin:0 0 0 11px;color:#c9d3df;font:bold 10px/19px Arial,sans-serif;white-space:nowrap;cursor:pointer}
        .check-label input{width:14px;height:14px;margin:0 7px 0 0;vertical-align:middle;-webkit-appearance:none;background:url('/store/img/layout/checkbox.gif') 0 -14px no-repeat;border:0;cursor:pointer}.check-label input:checked{background-position:0 0}
        .filter-label{display:block;margin:6px 0 3px 11px;color:#4fa3e8;font-size:10px}.sort-select{display:block;width:124px;height:19px;margin:0 0 4px 11px;padding:1px 22px 1px 4px;-webkit-appearance:none;background:#000 url('/store/img/layout/downBtn.png') 100% 0 no-repeat;color:#fff;border:1px solid #28445f;font:11px Arial,sans-serif}.sort-select option{background:#000;color:#fff}
        /* catalog grid */
        .catalog-panel{position:absolute;top:20px;right:6px;bottom:9px;left:173px}
        .catalog-scroll{position:absolute;top:0;right:0;bottom:0;left:0;overflow-y:scroll;overflow-x:hidden}.catalog-scroll::-webkit-scrollbar{width:10px;background:#08101a}.catalog-scroll::-webkit-scrollbar-track{background:#0a1320;border:1px solid #1a2a3e}.catalog-scroll::-webkit-scrollbar-thumb{background:#1a2f48;border:1px solid #2c4a6b;-webkit-border-radius:4px}
        .products{overflow:hidden;padding-bottom:2px}.product{float:left;width:160px;margin:0 12px 12px 0;padding:0;text-align:center}
        .card-body{position:relative;width:158px;height:213px;border:1px solid #203a54;background:#000 url('/store/img/layout/newitemcard.jpg') 0 0 no-repeat;-webkit-border-radius:3px;border-radius:3px;overflow:hidden}
        .card-link{display:block;position:absolute;top:0;right:0;bottom:0;left:0;color:#ddd;text-decoration:none}.card-link:hover{text-decoration:none}
        .card-art{position:absolute;top:1px;left:1px;width:156px;height:210px;display:block}.missing-art{display:block;height:100%;background:#090e14}
        .card-title{position:absolute;top:0;left:0;right:0;height:22px;background:url('/store/img/layout/cardoverlay_top.png') 0 0 repeat-x;color:#fff;font:12px/20px Arial,sans-serif;text-align:center;overflow:hidden;white-space:nowrap}
        .card-footer{position:absolute;left:0;right:0;bottom:0;height:53px;background:#000;background:rgba(0,0,0,.62);color:#fff;font-size:11px}
        .card-footer .card-action{position:absolute;top:8px;left:31px}.card-footer .price{position:absolute;left:0;right:0;bottom:6px;text-align:center;white-space:nowrap}
        .card-action{display:block;width:94px;height:22px;line-height:22px;margin:0;padding:0;background:#11263b url('/store/img/layout/unlockbutton.png') 0 -22px no-repeat;border:0;color:#d9e7f5;text-align:center;font:13px/22px Arial,sans-serif;white-space:nowrap;overflow:hidden}
        .card-action:hover{text-decoration:none;color:#fff;background-position:0 -44px}
        .card-action.disabled{position:relative;overflow:visible;background:none;color:#e0161c;font-size:11px;cursor:default}.card-action.disabled:before{content:"";position:absolute;top:0;left:0;width:94px;height:22px;background:url('/store/img/layout/unlockbutton.png') 0 -22px no-repeat;opacity:.4}.card-action.disabled span{position:relative}
        .price{display:inline-block;color:#fff}.pr-rp,.pr-ip{white-space:nowrap}.pr-rp img,.pr-ip img{width:13px;height:13px;margin:0 3px 0 0;vertical-align:-2px}.pr-or{margin:0 5px;color:#aab4be;font-size:10px}
        .pr-ip.short{color:#e0161c}.price s{color:#8a96a3}
        .card-badge{position:absolute;z-index:3;width:48px;height:49px;color:#fff;font:bold 10px/10px Arial,sans-serif;text-align:center;pointer-events:none}
        .card-badge.sale{top:24px;right:2px;background:url('/store/img/layout/sale_sunburst.png') 0 0 no-repeat;padding-top:12px;-webkit-box-sizing:border-box;box-sizing:border-box}
        .card-badge.fresh{top:20px;left:-8px;width:73px;background:url('/store/img/layout/New.png') 0 0 no-repeat;font-size:11px;line-height:49px}
        .card-badge.hot{top:0;left:0;width:37px;height:37px;background:url('/store/img/layout/hoticon2.png') 0 0 no-repeat}
        .icon-card .card-body{background:#08111c}.icon-card .card-footer{background:none}.icon-card .card-art{top:58px;left:29px;width:100px;height:100px}
        .rune-products .product{width:160px}.rune-card .card-body{height:170px;background:#060d17;border-color:#203a54}
        .rune-header{position:absolute;top:0;left:0;right:0;height:64px;overflow:hidden;text-align:center}.rune-card .card-title{position:static;height:auto;min-height:26px;padding:7px 4px 0;background:none;color:#7db8ea;font:10px/12px Arial,sans-serif;white-space:normal}
        .rune-effect{display:block;padding:2px 6px 0;color:#fff;font-size:11px;line-height:13px}.rune-image{position:absolute;top:66px;left:55px;width:48px;height:48px}.rune-art{display:block;width:48px;height:48px}
        .rune-card .card-footer{height:42px;background:none}.rune-card .card-footer .card-action{top:0}.rune-card .card-footer .price{bottom:2px}
        .empty{padding:40px 12px;color:#a3b0bd;text-align:center}
        /* store home (Reconstruction: item selection; geometry measured) */
        .home-dashboard{position:absolute;top:0;right:0;bottom:0;left:0}
        .home-hero{position:absolute;top:11px;left:10px;width:776px;height:346px;border:2px solid #1a2d4b;background:#01324f;overflow:hidden}
        .promo-slide{visibility:hidden;position:absolute;top:0;left:0;width:776px;height:346px;background:#05202e;background:-webkit-gradient(linear,left top,left bottom,from(#020b10),color-stop(.25,#05202e),to(#0d2d46));overflow:hidden}.promo-slide.on{visibility:visible}
        /* The sale banner, measured on the 25 September 2011 Homepage screenshot (ruler-home-banner-*): three abutting parallelogram panels
           ~150 px wide whose left edges lean ~6 degrees (top x 375/524/674, bottom x 339/487/638 inside the 776x346 banner), the skin art
           scaled to the full banner height and cropped on the middle of the portrait. Reconstruction: the art is the shipped 307x557 loading
           portrait (already centred on the character) instead of per-item focal crops of the 1215x717 splash. */
        .promo-art{position:absolute;top:0;left:0;width:776px;height:346px;overflow:hidden}
        .bn-panel{position:absolute;top:0;width:150px;height:346px;overflow:hidden;-webkit-transform:skewX(-6deg)}
        .bn-panel img{position:absolute;top:-3px;left:-22px;width:194px;height:352px;-webkit-transform:skewX(6deg)}
        .bn1{left:357px}.bn2{left:506px}.bn3{left:656px}
        .promo-seal{position:absolute;top:41px;left:12px;width:84px;height:84px;background:url('/store/img/home/sale-seal-blue.png') 0 0 no-repeat;color:#fff;font:bold 14px/14px Arial,sans-serif;text-align:center}
        .promo-seal b{display:block;padding-top:25px;font:bold 25px/28px Arial,sans-serif}.promo-seal i{display:block;font-style:normal;font:bold 15px/16px Arial,sans-serif}
        .promo-new{position:absolute;top:34px;left:10px;width:90px;height:90px;background:url('/store/img/layout/New.png') 50% 50% no-repeat;-webkit-background-size:90px 62px;background-size:90px 62px;color:#fff;font:bold 19px/90px {{FrizStack}};text-align:center}
        .promo-title{position:absolute;top:34px;left:108px;width:380px;margin:0;color:#fff9ed;font:normal 30px/36px {{FrizStack}};text-transform:uppercase;white-space:nowrap}
        .promo-expires{position:absolute;top:79px;left:111px;color:#5ebaf9;font:14px/18px Arial,sans-serif}
        .promo-text{position:absolute;top:139px;left:56px;width:233px;color:#e3dfae;font:14px/18px Arial,sans-serif;text-align:center}.promo-text b{font-weight:bold}
        .promo-button{position:absolute;top:236px;left:64px;display:block;width:210px;height:42px;line-height:42px;border:1px solid #d79b55;background:#a8561f;background:-webkit-gradient(linear,left top,left bottom,from(#a8561f),to(#7b3207));-webkit-border-radius:7px;border-radius:7px;color:#fff;font:15px/42px Arial,sans-serif;text-align:center}.promo-button:hover{text-decoration:none;background:#c06d1d}
        .promo-pager{position:absolute;right:5px;bottom:11px;overflow:hidden}.promo-pager a{float:left;display:block;width:20px;height:20px;margin:0 0 0 7px;border:1px solid #3a3a30;background:#0a0b0c;background:rgba(10,11,12,.85);color:#b9a77d;font:16px/20px {{FrizStack}};text-align:center}.promo-pager a.on,.promo-pager a:hover{border-color:#fff;color:#fff;text-decoration:none}
        .home-whats-new{position:absolute;top:367px;left:10px;width:778px;height:274px;border:1px solid #192938;background:#05090e}
        .home-whats-new h2,.home-sidebar h2{display:block;height:29px;margin:0;padding:0 9px;color:#daeffe;font:normal 14px/29px {{FrizStack}};background:#081120;background:-webkit-gradient(linear,left top,left bottom,from(#1f3b63),color-stop(.1,#1a2c44),color-stop(.55,#0b1a2c),to(#081120));border-bottom:1px solid #09192a}
        .home-cards{position:absolute;top:30px;left:0;right:0;bottom:0;padding:14px 0 0 30px;overflow-y:scroll;overflow-x:hidden}.home-cards::-webkit-scrollbar{width:10px;background:#08101a}.home-cards::-webkit-scrollbar-track{background:#0a1320}.home-cards::-webkit-scrollbar-thumb{background:#1a2f48;border:1px solid #2c4a6b;-webkit-border-radius:4px}
        .featured-card{float:left;width:160px;margin:0 17px 14px 0}.featured-card .card-body:hover:after{content:"";position:absolute;top:-1px;left:-1px;width:159px;height:214px;background:url('/store/img/layout/whatsnew_over.png') 0 0 no-repeat;pointer-events:none;z-index:4}
        .home-sidebar{position:absolute;top:10px;left:800px;width:270px}
        .sidebar-panel{margin:0 0 7px;border:1px solid #192938;background:#05090e}
        .sidebar-feature{position:relative;height:111px}.sidebar-feature-art{position:absolute;top:13px;left:14px;display:block;width:50px;height:50px;border:3px solid #203a54;overflow:hidden;background:#000}.sidebar-feature-art img{display:block;width:50px;height:67px;margin-top:-4px}
        .sidebar-feature-copy{position:absolute;top:19px;left:80px;right:8px;color:#fff;font:12px Arial,sans-serif}.sidebar-feature-copy strong{display:block;font-weight:normal}
        .sidebar-feature-copy .card-action{margin:14px 0 4px}.sidebar-feature-copy .price{font-size:11px}
        .sidebar-products{height:456px;overflow:hidden}.sidebar-product{position:relative;height:58px;overflow:hidden}.sidebar-art{position:absolute;top:5px;left:11px;display:block;width:42px;height:42px;border:2px solid #203a54;overflow:hidden;background:#000}.sidebar-art img{display:block;width:42px;height:57px;margin-top:-3px}
        .sidebar-copy{position:absolute;top:6px;left:68px;right:6px;color:#fff;font:12px/14px Arial,sans-serif}.sidebar-copy strong{display:block;font-weight:normal;white-space:nowrap;overflow:hidden}.sidebar-copy .why{display:block;color:#e0161c;font-size:10px;line-height:12px}.sidebar-copy .price{font-size:11px}
        .sidebar-product .hot{position:absolute;top:4px;right:4px;width:20px;height:20px;background:url('/store/img/layout/hotIcon.png') 0 0 no-repeat;-webkit-background-size:20px 20px;background-size:20px 20px}
        /* detail, purchases, codes, results (Reconstruction: no 2011 capture of these pages) */
        .balance{background:#0b111a;border:1px solid #2d4256;padding:10px}.purchase{display:inline-block;margin:5px 8px 5px 0}
        button{background:#1d527f;border:1px solid #75a3c3;-webkit-border-radius:4px;border-radius:4px;color:#fff;padding:8px 12px;cursor:pointer}
        .purchase button,.code-panel button{background:#1d527f;background:-webkit-gradient(linear,left top,left bottom,from(#8fc0e4),color-stop(.14,#397eb6),to(#173e68));border-color:#7099b8;-webkit-box-shadow:inset 0 1px 1px #6b9abd;box-shadow:inset 0 1px 1px #6b9abd}
        .owned{color:#83ba91;font-weight:bold}.muted{color:#abb8c3}
        /* The unlock dialog (retail opened it over the grid; Purchase Item, X, the item card, a balance / price / remainder table per
           currency and the Unlock With buttons: March 2010 captures, 2011 art). Reconstruction: the placement over the shipped art
           (price_table_bg 332x200 = RP and IP columns, purchase_buttons = 116x38 cells in three states, close_button, sale_complete,
           sorry, error) and the CSS frame, which stands in for the retail dialog's own background. */
        .dlg-wrap{position:absolute;top:0;right:0;bottom:0;left:137px;background:#000;background:rgba(0,0,0,.74);z-index:20}
        .dlg{position:absolute;left:50%;top:50%;width:556px;height:326px;margin:-163px 0 0 -278px;border:2px solid #5d5d5d;-webkit-border-radius:8px;border-radius:8px;background:#101010;background:-webkit-gradient(linear,left top,left bottom,from(#262626),to(#050505));-webkit-box-shadow:0 0 22px #000;box-shadow:0 0 22px #000;font:12px Arial,sans-serif;color:#fff}
        .dlg-title{position:absolute;left:16px;top:10px;color:#2d9be3;font:15px/18px Arial,sans-serif}
        .dlg-close{position:absolute;right:10px;top:9px;width:20px;height:20px;background:url('/store/img/layout/close_button.png') 0 0 no-repeat}.dlg-close:hover{background-position:0 -20px}
        .dlg-card{position:absolute;left:18px;top:40px;width:160px;height:215px}
        .dlg-table{position:absolute;top:40px;width:155px;height:200px;background:url('/store/img/layout/price_table_bg.png') 0 0 no-repeat}.dlg-table.ip{background-position:-177px 0}
        .dlg-table span{position:absolute;left:0;width:155px;text-align:center;font:bold 15px/18px Arial,sans-serif;color:#fff}
        .dlg-bal{top:62px}.dlg-price{top:90px;font-size:14px;color:#e9e9e9}.dlg-rem{top:118px}.dlg-rem.neg{color:#e0161c}
        .dlg-with{position:absolute;top:254px;width:116px;text-align:center;font:bold 12px/14px Arial,sans-serif;color:#fff}
        .dlg-unlock{position:absolute;top:270px;display:block;width:116px;height:38px;margin:0;padding:0;border:0;background:url('/store/img/layout/purchase_buttons.png') 0 0 no-repeat;-webkit-border-radius:0;border-radius:0;-webkit-box-shadow:none;box-shadow:none;cursor:pointer;font-size:0;color:transparent;text-indent:-999px;overflow:hidden}
        .dlg-unlock.ip{background-position:-116px 0}.dlg-unlock:hover{background-position:0 -38px}.dlg-unlock.ip:hover{background-position:-116px -38px}
        .dlg-unlock.off{background-position:0 -76px;cursor:default}.dlg-unlock.ip.off{background-position:-116px -76px}
        .dlg-short{position:absolute;top:310px;width:155px;text-align:center;color:#e0161c;font:11px/12px Arial,sans-serif}
        .dlg-qty{position:absolute;left:18px;top:262px;color:#c9d3df;font:bold 11px/20px Arial,sans-serif}.dlg-qty select{width:46px;height:19px;margin-left:6px;padding:1px 22px 1px 4px;-webkit-appearance:none;background:#000 url('/store/img/layout/downBtn.png') 100% 0 no-repeat;color:#fff;border:1px solid #28445f;font:11px Arial,sans-serif;vertical-align:middle}.dlg-qty select option{background:#000;color:#fff}
        .dlg-sorry{position:absolute;left:280px;top:164px;width:158px;height:134px;background:url('/store/img/layout/sorry.png') 0 0 no-repeat}
        .dlg-banner{position:absolute;left:196px;top:46px;width:329px;height:39px;background:url('/store/img/layout/error.gif') 0 0 no-repeat;color:#fff;font:bold 13px/39px Arial,sans-serif;text-align:center;overflow:hidden;white-space:nowrap}
        .dlg-note{position:absolute;left:196px;top:94px;width:329px;height:64px;overflow:hidden;color:#d6dde5;font:13px/18px Arial,sans-serif}.dlg-note a{color:#5ebaf9}
        .dlg-done{position:absolute;right:22px;bottom:18px;display:block;width:110px;height:30px;border:1px solid #5a9be0;background:#1b5cae;background:-webkit-gradient(linear,left top,left bottom,from(#4a9be8),color-stop(.5,#1f66b8),to(#0e3b78));-webkit-border-radius:3px;border-radius:3px;color:#fff;font:bold 14px/30px Arial,sans-serif;text-align:center}.dlg-done:hover{text-decoration:none;background:#2a74c8;background:-webkit-gradient(linear,left top,left bottom,from(#6cb4f5),to(#1550a0))}
        .dlg.done{width:511px;height:316px;margin:-158px 0 0 -255px;border:2px solid transparent;background:url('/store/img/layout/sale_complete.png') 0 0 no-repeat;-webkit-border-radius:0;border-radius:0}
        .dlg.done .dlg-card{left:18px;top:62px}.dlg.done .pr-ip.short{color:#fff}/* the card of "Item Unlocked!" is no warning: retail's (Shop_7) shows no price, and the balance after paying must not paint it red */.dlg-head{position:absolute;left:0;top:34px;width:511px;text-align:center;color:#f4e21a;font:bold 28px/34px {{FrizStack}}}
        .dlg-copy{position:absolute;left:196px;top:96px;width:290px;color:#e4e8ed;font:14px/19px Arial,sans-serif}
        /* Card hover (the 1.38 look in the mid-2011 captures: a dimmed card with a blue frame, the magnifier over the upper half and Unlock
           over the lower half; mangify.png holds the faded and the solid magnifier). Upper half = the client's own champion window, lower
           half = the unlock dialog; the 1.0.0.125 notes add the standalone 94x22 Unlock button, which stays in the footer. */
        .card-info,.card-buy{position:absolute;left:0;right:0;display:none;z-index:3;text-decoration:none}
        .card-body:hover{border-color:#3c8fd0}.card-body:hover .card-info,.card-body:hover .card-buy{display:block}
        .card-info{top:0;height:106px;background:#000;background:rgba(0,0,0,.5);cursor:pointer}.card-info .mag{position:absolute;left:47px;top:22px;width:64px;height:64px;background:url('/store/img/layout/mangify.png') 0 0 no-repeat}.card-info:hover .mag{background-position:0 -64px}
        .card-buy{top:106px;height:107px;background:#000;background:rgba(0,0,0,.72);color:#e1d357;font:17px/74px {{FrizStack}};text-align:center}.card-buy:hover{color:#fff27a;text-decoration:none}
        .rune-card .card-info,.icon-card .card-info{display:none!important}.rune-card .card-buy{top:0;height:128px;line-height:128px}.icon-card .card-buy{top:0;height:160px;line-height:160px}
        .purchases{padding:24px;min-height:430px}.purchases h1{height:26px;line-height:26px;margin:0 0 15px;padding:0 12px;color:#e0e5e9;font:normal 13px/26px {{FrizStack}};background:#000c1a url('/store/img/layout/header_bg4b.jpg') left top no-repeat;border:1px solid #385875}.purchase-scroll{overflow-x:auto;border:1px solid #354b5f;background:#08111c}.purchase-history{border-collapse:collapse;width:100%;min-width:650px}.purchase-history td,.purchase-history th{border-bottom:1px solid #2b3c4d;padding:8px;text-align:left;white-space:nowrap}.purchase-history th{color:#dbe3e9;background:#000c1a url('/store/img/layout/header_bg4b.jpg') left top no-repeat}.purchase-history td:nth-child(2){white-space:normal;min-width:180px}
        .store-rp .store-panel{background:#000 url('/store/img/layout/purchase_background.png') repeat-x}.rp-information{padding:26px 30px}.rp-information h1{height:26px;line-height:26px;margin:0 0 22px;padding:0 12px;color:#e0e5e9;font:normal 13px/26px {{FrizStack}};background:#000c1a url('/store/img/layout/header_bg4b.jpg') left top no-repeat;border:1px solid #385875}.rp-overview{overflow:hidden}.rp-balance-card{position:relative;float:left;width:259px;height:220px;background:url('/store/img/layout/riotpoint_bg.png') 0 0 no-repeat}.rp-balance-card span{position:absolute;top:152px;left:18px;color:#aebbc9;font-size:12px}.rp-balance-card strong{position:absolute;top:171px;left:18px;color:#edce87;font:bold 24px {{FrizStack}}}.rp-copy{min-height:174px;margin-left:285px;padding:18px 24px;background:#0a121c;border:1px solid #3f5368}.rp-copy h2{margin:0 0 12px;color:#dce4e9;font:normal 17px {{FrizStack}}}.rp-copy p{line-height:1.5}.back-link{color:#a9c9dc}
        .store-codes .store-panel{background:#000}.store-codes .purchases{min-height:0;padding-top:292px;background:url('/store/img/layout/redeem_code.png') center top no-repeat}.code-panel{max-width:650px;margin:0 auto;padding:15px 20px;background:#09131f;border:1px solid #3b536b}.code-panel h1{font-size:13px}.code-panel .purchase{display:block}.code-panel input[type=text]{width:220px;margin:0 10px;padding:7px;background:#050a10;border:1px solid #607991;color:#eee}.code-panel .balance{display:block;margin:12px 0 0;color:#b8cad9}
        .store-result .purchases,.store-error .purchases{padding-top:45px}.store-result .purchases>p,.store-error .purchases>p{max-width:660px}.store-result .balance,.store-error .balance{background:#0a1724}
        </style></head>
        <body onload="storeLoaded()"{{bodyAttributes}}><main>{{body}}</main><script type="text/javascript">{{StoreScript}}storeFocusCard();</script></body></html>
        """;

    // Both prices of an item: "RP 975 OR IP 6300", with the sale's regular RP price struck through. The IP price turns
    // red when the account cannot afford it; the RP price never does. Exact, from the 25 September 2011 store screenshots
    // (reference/air-pages-2011/store-screens-20110925) of an account with 640 RP and 568 IP: Champpage.PNG prints 975 RP in
    // white beside 6300 and 4800 IP in red, Skinpage.PNG prints 975 RP skins in white, Runepage.PNG prints 1025, 820 and 2050 IP
    // in red and 515, 410 and 205 IP in white. The 2010 captures (Shop_1, Shop_5) show no colour either. So both currencies do not
    // behave alike in retail, and the batch 96 note "a short RP balance never turns a card price red" is the retail rule.
    private static string FormatPrice(PlatformStoreItem item, PlatformEconomyState? economy = null)
    {
        var parts = new List<string>();
        if (item.RpPrice is int rp)
        {
            var regular = item.SalePercent > 0 && item.RegularRpPrice is int old
                ? $"<s>{old.ToString(CultureInfo.InvariantCulture)}</s> " : string.Empty;
            parts.Add($"<span class=pr-rp title=\"Premium Points\"><img src=\"/store/img/layout/rp_logo2.png\" alt=\"\">{regular}{rp.ToString(CultureInfo.InvariantCulture)}</span>");
        }
        if (item.IpPrice is int ip)
        {
            var short_ = economy is not null && economy.Ip < ip ? " short" : string.Empty;
            parts.Add($"<span class=\"pr-ip{short_}\" title=\"Influence Points\"><img src=\"/store/img/layout/ip_logo2.png\" alt=\"\">{ip.ToString(CultureInfo.InvariantCulture)}</span>");
        }
        return parts.Count == 0 ? "Unavailable" : string.Join("<span class=pr-or>OR</span>", parts);
    }
}
