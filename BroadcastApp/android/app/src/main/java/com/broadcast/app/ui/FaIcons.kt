package com.broadcast.app.ui

/*
 * Font Awesome 6.7.2 字形表（**手工维护**：历史上由 tools/make_icons.py 生成，那个脚本已不在仓库里）。
 * 加图标的方法：从 Font Awesome 官网查到 free-solid 的码点，在这里加常量 + MAP 一项即可；
 * 字体是完整的 fa-solid-900.otf（3235 个字形，见 res/font/），所以只要码点对就能显示。
 * 缺图标不会崩：glyph() 会退回 circle-question（问号）。
 *
 * Font Awesome Free 6.7.2 — https://fontawesome.com
 * 图标: CC BY 4.0 | 字体: SIL OFL 1.1 | 代码: MIT
 * 许可证全文见 app/src/main/assets/fontawesome/LICENSE.txt
 */
object FaIcons {

    const val FA_CAMERA               = "\uF030"   // camera
    const val FA_CLIPBOARD_CHECK      = "\uF274"   // clipboard-check
    const val FA_USER_PLUS            = "\uF234"   // user-plus
    const val FA_ROTATE               = "\uF2F1"   // rotate
    const val FA_GEAR                 = "\uF013"   // gear
    const val FA_CIRCLE_PLAY          = "\uF144"   // circle-play
    const val FA_WAND_MAGIC_SPARKLES  = "\uE2CA"   // wand-magic-sparkles
    const val FA_CAMERA_ROTATE        = "\uE0D8"   // camera-rotate
    const val FA_VOLUME_HIGH          = "\uF028"   // volume-high
    const val FA_VOLUME_XMARK         = "\uF6A9"   // volume-xmark
    const val FA_CIRCLE_INFO          = "\uF05A"   // circle-info
    const val FA_TRIANGLE_EXCLAMATION = "\uF071"   // triangle-exclamation
    const val FA_GAUGE_HIGH           = "\uF625"   // gauge-high
    const val FA_SLIDERS              = "\uF1DE"   // sliders
    const val FA_MAGNIFYING_GLASS     = "\uF002"   // magnifying-glass
    const val FA_CHART_SIMPLE         = "\uE473"   // chart-simple
    const val FA_CLOCK                = "\uF017"   // clock
    const val FA_BOLT                 = "\uF0E7"   // bolt
    const val FA_RIGHT_TO_BRACKET     = "\uF2F6"   // right-to-bracket
    const val FA_RIGHT_FROM_BRACKET   = "\uF2F5"   // right-from-bracket
    const val FA_ARROWS_ROTATE        = "\uF021"   // arrows-rotate
    const val FA_FILE_EXPORT          = "\uF56E"   // file-export
    const val FA_CLIPBOARD_LIST       = "\uF46D"   // clipboard-list
    const val FA_USER_CHECK           = "\uF4FC"   // user-check
    const val FA_CIRCLE_CHECK         = "\uF058"   // circle-check
    const val FA_CIRCLE_XMARK         = "\uF057"   // circle-xmark
    const val FA_LIST_CHECK           = "\uF0AE"   // list-check
    const val FA_CALENDAR_DAYS        = "\uF073"   // calendar-days
    const val FA_IMAGE                = "\uF03E"   // image
    const val FA_IMAGES               = "\uF302"   // images
    const val FA_SCISSORS             = "\uF0C4"   // scissors
    const val FA_FILE_CSV             = "\uF6DD"   // file-csv
    const val FA_FILE_IMPORT          = "\uF56F"   // file-import
    const val FA_USERS                = "\uF0C0"   // users
    const val FA_PEN_TO_SQUARE        = "\uF044"   // pen-to-square
    const val FA_TRASH_CAN            = "\uF2ED"   // trash-can
    const val FA_SHARE_NODES          = "\uF1E0"   // share-nodes
    const val FA_FOLDER_OPEN          = "\uF07C"   // folder-open
    const val FA_FLASK                = "\uF0C3"   // flask
    const val FA_FILE_ZIPPER          = "\uF1C6"   // file-zipper
    const val FA_CHECK                = "\uF00C"   // check
    const val FA_XMARK                = "\uF00D"   // xmark
    const val FA_PLUS                 = "\uF067"   // plus
    const val FA_MINUS                = "\uF068"   // minus
    const val FA_PLAY                 = "\uF04B"   // play
    const val FA_DOWNLOAD             = "\uF019"   // download
    const val FA_UPLOAD               = "\uF093"   // upload
    const val FA_CIRCLE_QUESTION      = "\uF059"   // circle-question
    const val FA_HOURGLASS_HALF       = "\uF252"   // hourglass-half
    const val FA_SPINNER              = "\uF110"   // spinner
    const val FA_ID_CARD              = "\uF2C2"   // id-card
    const val FA_FORWARD              = "\uF04E"   // forward
    const val FA_USER_MINUS           = "\uF503"   // user-minus
    const val FA_USER_XMARK           = "\uF235"   // user-xmark
    const val FA_ERASER               = "\uF12D"   // eraser
    const val FA_CHEVRON_DOWN         = "\uF078"   // chevron-down
    const val FA_MICROPHONE           = "\uF130"   // microphone
    const val FA_MICROPHONE_SLASH     = "\uF131"   // microphone-slash
    const val FA_QRCODE               = "\uF029"   // qrcode
    const val FA_TOWER_BROADCAST      = "\uF519"   // tower-broadcast
    const val FA_WIFI                 = "\uF1EB"   // wifi
    const val FA_PLUG                 = "\uF1E6"   // plug
    const val FA_SERVER               = "\uF233"   // server
    const val FA_NETWORK_WIRED        = "\uF6FF"   // network-wired
    const val FA_SHIELD_HALVED        = "\uF3ED"   // shield-halved
    const val FA_USER_SHIELD          = "\uF505"   // user-shield
    const val FA_HANDSHAKE            = "\uF2B5"   // handshake
    const val FA_LIST                 = "\uF03A"   // list
    const val FA_CALENDAR_CHECK       = "\uF274"   // calendar-check
    const val FA_BELL                 = "\uF0F3"   // bell
    const val FA_COMMENT_DOTS         = "\uF4AD"   // comment-dots
    const val FA_CHART_LINE           = "\uF201"   // chart-line
    const val FA_TABLE_LIST           = "\uF00B"   // table-list

    // 这一轮界面改版补的图标（播控拖动条 / 顶栏加号 / 排程调休编辑用）
    const val FA_CIRCLE_PLUS          = "\uF055"   // circle-plus
    const val FA_PAUSE                = "\uF04C"   // pause
    const val FA_STOP                 = "\uF04D"   // stop
    const val FA_FORWARD_STEP         = "\uF051"   // forward-step
    const val FA_BACKWARD_STEP        = "\uF048"   // backward-step
    const val FA_VOLUME_LOW           = "\uF027"   // volume-low
    const val FA_REPEAT               = "\uF01E"   // repeat
    const val FA_SHUFFLE              = "\uF074"   // shuffle
    const val FA_TRASH                = "\uF1F8"   // trash
    const val FA_FLOPPY_DISK          = "\uF0C7"   // floppy-disk
    const val FA_CALENDAR_PLUS        = "\uF271"   // calendar-plus
    const val FA_KEY                  = "\uF084"   // key
    const val FA_ARROW_LEFT           = "\uF060"   // arrow-left
    const val FA_SIGNAL               = "\uF012"   // signal

    private val MAP = mapOf(
        "camera" to FA_CAMERA,
        "clipboard-check" to FA_CLIPBOARD_CHECK,
        "user-plus" to FA_USER_PLUS,
        "rotate" to FA_ROTATE,
        "gear" to FA_GEAR,
        "circle-play" to FA_CIRCLE_PLAY,
        "wand-magic-sparkles" to FA_WAND_MAGIC_SPARKLES,
        "camera-rotate" to FA_CAMERA_ROTATE,
        "volume-high" to FA_VOLUME_HIGH,
        "volume-xmark" to FA_VOLUME_XMARK,
        "circle-info" to FA_CIRCLE_INFO,
        "triangle-exclamation" to FA_TRIANGLE_EXCLAMATION,
        "gauge-high" to FA_GAUGE_HIGH,
        "sliders" to FA_SLIDERS,
        "magnifying-glass" to FA_MAGNIFYING_GLASS,
        "chart-simple" to FA_CHART_SIMPLE,
        "clock" to FA_CLOCK,
        "bolt" to FA_BOLT,
        "right-to-bracket" to FA_RIGHT_TO_BRACKET,
        "right-from-bracket" to FA_RIGHT_FROM_BRACKET,
        "arrows-rotate" to FA_ARROWS_ROTATE,
        "file-export" to FA_FILE_EXPORT,
        "clipboard-list" to FA_CLIPBOARD_LIST,
        "user-check" to FA_USER_CHECK,
        "circle-check" to FA_CIRCLE_CHECK,
        "circle-xmark" to FA_CIRCLE_XMARK,
        "list-check" to FA_LIST_CHECK,
        "calendar-days" to FA_CALENDAR_DAYS,
        "image" to FA_IMAGE,
        "images" to FA_IMAGES,
        "scissors" to FA_SCISSORS,
        "file-csv" to FA_FILE_CSV,
        "file-import" to FA_FILE_IMPORT,
        "users" to FA_USERS,
        "pen-to-square" to FA_PEN_TO_SQUARE,
        "trash-can" to FA_TRASH_CAN,
        "share-nodes" to FA_SHARE_NODES,
        "folder-open" to FA_FOLDER_OPEN,
        "flask" to FA_FLASK,
        "file-zipper" to FA_FILE_ZIPPER,
        "check" to FA_CHECK,
        "xmark" to FA_XMARK,
        "plus" to FA_PLUS,
        "minus" to FA_MINUS,
        "play" to FA_PLAY,
        "download" to FA_DOWNLOAD,
        "upload" to FA_UPLOAD,
        "circle-question" to FA_CIRCLE_QUESTION,
        "hourglass-half" to FA_HOURGLASS_HALF,
        "spinner" to FA_SPINNER,
        "id-card" to FA_ID_CARD,
        "forward" to FA_FORWARD,
        "user-minus" to FA_USER_MINUS,
        "user-xmark" to FA_USER_XMARK,
        "eraser" to FA_ERASER,
        "chevron-down" to FA_CHEVRON_DOWN,
        "microphone" to FA_MICROPHONE,
        "microphone-slash" to FA_MICROPHONE_SLASH,
        "qrcode" to FA_QRCODE,
        "tower-broadcast" to FA_TOWER_BROADCAST,
        "wifi" to FA_WIFI,
        "plug" to FA_PLUG,
        "server" to FA_SERVER,
        "network-wired" to FA_NETWORK_WIRED,
        "shield-halved" to FA_SHIELD_HALVED,
        "user-shield" to FA_USER_SHIELD,
        "handshake" to FA_HANDSHAKE,
        "list" to FA_LIST,
        "calendar-check" to FA_CALENDAR_CHECK,
        "bell" to FA_BELL,
        "comment-dots" to FA_COMMENT_DOTS,
        "chart-line" to FA_CHART_LINE,
        "table-list" to FA_TABLE_LIST,
        "circle-plus" to FA_CIRCLE_PLUS,
        "pause" to FA_PAUSE,
        "stop" to FA_STOP,
        "forward-step" to FA_FORWARD_STEP,
        "backward-step" to FA_BACKWARD_STEP,
        "volume-low" to FA_VOLUME_LOW,
        "repeat" to FA_REPEAT,
        "shuffle" to FA_SHUFFLE,
        "trash" to FA_TRASH,
        "floppy-disk" to FA_FLOPPY_DISK,
        "calendar-plus" to FA_CALENDAR_PLUS,
        "key" to FA_KEY,
        "arrow-left" to FA_ARROW_LEFT,
        "signal" to FA_SIGNAL,
    )

    /** 取字形；没有的图标退化成问号，界面上不会崩。 */
    fun glyph(name: String): String = MAP[name] ?: MAP["circle-question"] ?: "?"

    fun has(name: String): Boolean = MAP.containsKey(name)

    val count: Int get() = MAP.size
}
