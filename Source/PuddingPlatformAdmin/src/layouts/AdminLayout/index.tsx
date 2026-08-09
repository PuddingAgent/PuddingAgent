import { ProLayout } from '@ant-design/pro-components';
import { Link, Outlet, history, useIntl, useLocation, useModel } from '@umijs/max';
import React from 'react';
import { adminRoutes } from '../../../config/routes';
import defaultSettings from '../../../config/defaultSettings';
import { PuddingGlobalActions } from '@/components/GlobalActions';
import { AvatarDropdown, AvatarName } from '@/components/RightContent/AvatarDropdown';

const loginPath = '/user/login';
const bootstrapPath = '/bootstrap';
const showDevTools =
  process.env.NODE_ENV === 'development' &&
  typeof window !== 'undefined' &&
  new URLSearchParams(window.location.search).has('debug');

const DevSettingDrawer = React.lazy(async () => {
  const module = await import('@ant-design/pro-components');
  return { default: module.SettingDrawer };
});

/**
 * 管理页专用壳层。
 *
 * 该组件仅作为管理路由的异步父路由加载，避免 Chat 首载执行 ProLayout
 * 以及管理页右上角操作区的运行时代码。
 */
const AdminLayout: React.FC = () => {
  const location = useLocation();
  const { formatMessage } = useIntl();
  const { initialState, setInitialState } = useModel('@@initialState');

  return (
    <ProLayout
      {...defaultSettings}
      {...initialState?.settings}
      route={{ path: '/', routes: adminRoutes }}
      location={location}
      formatMessage={formatMessage}
      menu={{ locale: true }}
      siderWidth={256}
      contentStyle={{ margin: 0 }}
      footerRender={false}
      bgLayoutImgList={[]}
      links={[]}
      menuHeaderRender={undefined}
      onMenuHeaderClick={(event) => {
        event.preventDefault();
        history.push('/');
      }}
      onPageChange={() => {
        if (
          !initialState?.currentUser &&
          location.pathname !== loginPath &&
          location.pathname !== bootstrapPath
        ) {
          history.push(loginPath);
        }
      }}
      menuItemRender={(menuItemProps, defaultDom) => {
        if (menuItemProps.isUrl || menuItemProps.children || !menuItemProps.path) {
          return defaultDom;
        }
        if (location.pathname === menuItemProps.path) {
          return defaultDom;
        }
        return (
          <Link to={menuItemProps.path.replace('/*', '')} target={menuItemProps.target}>
            {defaultDom}
          </Link>
        );
      }}
      actionsRender={() => [
        <PuddingGlobalActions
          key="global-actions"
          variant="pro-layout"
          setInitialState={setInitialState}
        />,
      ]}
      avatarProps={{
        src: initialState?.currentUser?.avatar,
        title: <AvatarName />,
        render: (_, avatarChildren) => (
          <AvatarDropdown dropdownTrigger={['click']}>{avatarChildren}</AvatarDropdown>
        ),
      }}
    >
      <Outlet />
      {showDevTools && (
        <React.Suspense fallback={null}>
          <DevSettingDrawer
            disableUrlParams
            enableDarkTheme
            settings={initialState?.settings}
            onSettingChange={(settings) => {
              setInitialState((previousState) => ({
                ...previousState,
                settings,
              }));
            }}
          />
        </React.Suspense>
      )}
    </ProLayout>
  );
};

export default AdminLayout;
